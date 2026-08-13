using AllaganLib.GameSheets.ItemSources;
using AllaganLib.GameSheets.Sheets.Helpers;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.Network;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using CSAchievement = FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement;

namespace GlamourLog.Services;

internal sealed unsafe class UnobtainableService : IPluginService, IDisposable {
    public int InitOrder => -10; // before CatalogService

    private readonly Hook<PacketDispatcher.Delegates.HandleAchievementsPacket>? _achievementsPacketHook;

    internal event System.Action? Changed;

    public UnobtainableService() {
        _achievementsPacketHook = Svc.Hook.HookFromAddress<PacketDispatcher.Delegates.HandleAchievementsPacket>((nint)PacketDispatcher.MemberFunctionPointers.HandleAchievementsPacket, HandleAchievementsPacketDetour);
        _achievementsPacketHook.Enable();

        Svc.ClientState.Login += OnLogin;
        Svc.ClientState.Logout += OnLogout;

        if (Svc.ClientState.IsLoggedIn)
            RequestAchievements();
    }

    public void Dispose() {
        Svc.ClientState.Logout -= OnLogout;
        Svc.ClientState.Login -= OnLogin;
        _achievementsPacketHook?.Dispose();
        Index = null;
    }

    [AllowNull]
    internal RepurchaseIndex Index { get => field ??= RepurchaseIndex.Build(); private set; }

    internal bool Apply(bool classificationUnobtainable, IReadOnlyList<uint> itemIds)
        => classificationUnobtainable.ApplyRepurchase(Index.EvaluateSetUnobtainable(itemIds));

    private void OnLogin() => RequestAchievements();
    private void OnLogout(int _, int __) => Index = null;

    private void RequestAchievements() {
        var ach = CSAchievement.Instance();
        if (ach is null)
            return;
        ach->RequestCompletedAchievements();
    }

    private void HandleAchievementsPacketDetour(AchievementsPacket* packet) {
        _achievementsPacketHook!.Original(packet);
        try {
            Changed?.Invoke();
        }
        catch (Exception ex) {
            Svc.Log.Error(ex, $"{nameof(UnobtainableService)} achievements packet");
        }
    }
}

internal enum RepurchaseShopKind : byte { GilShop, SpecialShop }

internal readonly record struct FestivalRef(ushort FestivalId, byte Begin, byte End);

internal sealed class RepurchaseListing {
    public required RepurchaseShopKind ShopKind { get; init; }
    public required uint ShopId { get; init; }
    public required ImmutableArray<uint> QuestIds { get; init; }
    public required uint AchievementId { get; init; }
    public required ImmutableArray<FestivalRef> FestivalRefs { get; init; }

    public bool HasGates => QuestIds.Length > 0 || AchievementId != 0 || FestivalRefs.Length > 0;

    public unsafe bool IsUnlocked(bool achievementsLoaded, out bool deferredAchievement) {
        deferredAchievement = false;
        foreach (var questId in QuestIds) {
            if (!QuestManager.IsQuestComplete(questId))
                return false;
        }
        if (AchievementId != 0) {
            if (!achievementsLoaded) {
                deferredAchievement = true;
                return false;
            }
            return CSAchievement.Instance()->IsComplete((int)AchievementId);
        }
        // festival-only (e.g. event-currency shop costs): not unlocked via quest completion
        return QuestIds.Length != 0 || FestivalRefs.Length != 0;
    }
}

internal sealed class RepurchaseIndex {
    private static readonly uint[] RecompenseOfficerNpcIds = [1017613, 1017614, 1017615];
    private readonly Dictionary<uint, ImmutableArray<RepurchaseListing>> _byItemId;

    private RepurchaseIndex(Dictionary<uint, ImmutableArray<RepurchaseListing>> byItemId)
        => _byItemId = byItemId;

    public static RepurchaseIndex Build() {
        var shopCache = Svc.SheetManager.NpcShopCache;
        var gilShopIds = new HashSet<uint>();
        var specialShopIds = new HashSet<uint>();

        foreach (var npcId in RecompenseOfficerNpcIds.Concat(HardcodedItems.CalamitySalvagers)) {
            if (shopCache.GetGilShopsByNpcId(npcId) is { } gil)
                gilShopIds.UnionWith(gil);
            if (shopCache.GetSpecialShopsByNpcId(npcId) is { } special)
                specialShopIds.UnionWith(special);
        }

        var acc = new Dictionary<uint, List<RepurchaseListing>>();
        foreach (var shopId in gilShopIds)
            IndexGilShop(shopId, acc);
        foreach (var shopId in specialShopIds)
            IndexSpecialShop(shopId, acc);

        return new RepurchaseIndex(acc.ToDictionary(kv => kv.Key, kv => kv.Value.ToImmutableArray()));
    }

    // null = leave ClassifyResult alone; true/false = force IsUnobtainable
    public unsafe bool? EvaluateSetUnobtainable(IReadOnlyList<uint> itemIds) {
        PieceResult? repurchase = null;
        PieceResult? seasonal = null;
        var achievementsLoaded = CSAchievement.Instance()->IsLoaded();

        foreach (var itemId in itemIds) {
            if (_byItemId.TryGetValue(itemId, out var listings) && listings.Length > 0)
                repurchase = FoldWorse(repurchase, EvaluatePiece(listings, achievementsLoaded));

            // event vendors (e.g. Dreamer) aren't on recompense; gate via shop festival / seasonal cost currencies
            seasonal = FoldWorse(seasonal, EvaluateSeasonalShopSources(itemId));
        }

        if (repurchase is PieceResult.Deferred)
            return null;
        if (repurchase is { } r)
            return r == PieceResult.Unobtainable;
        if (seasonal is { } s)
            return s == PieceResult.Unobtainable;
        return null;
    }

    private enum PieceResult : byte { Obtainable, Unobtainable, Deferred }

    // deferred > unobtainable > obtainable; null = nothing yet
    private static PieceResult? FoldWorse(PieceResult? acc, PieceResult? next) {
        if (next is null)
            return acc;
        if (acc is null)
            return next;
        if (acc is PieceResult.Deferred || next is PieceResult.Deferred)
            return PieceResult.Deferred;
        if (acc is PieceResult.Unobtainable || next is PieceResult.Unobtainable)
            return PieceResult.Unobtainable;
        return PieceResult.Obtainable;
    }

    [Flags]
    private enum LockedListingFlags : byte {
        None = 0,
        Locked = 1,
        FestivalActive = 2,
        AlwaysAvailableQuest = 4,
        DeferredAchievement = 8,
    }

    private static PieceResult EvaluatePiece(ImmutableArray<RepurchaseListing> listings, bool achievementsLoaded) {
        var locked = LockedListingFlags.None;

        foreach (var listing in listings) {
            if (!listing.HasGates)
                return PieceResult.Obtainable;

            if (listing.IsUnlocked(achievementsLoaded, out var deferredAch))
                return PieceResult.Obtainable;

            // festival refs include achievement -> quest links at this point
            if (listing.FestivalRefs.Length > 0) {
                if (IsAnyFestivalActive(listing.FestivalRefs)) {
                    locked |= LockedListingFlags.FestivalActive; // has festival and is active -> obtainable
                }
                else if (deferredAch) {
                    locked |= LockedListingFlags.DeferredAchievement; // out of season but may have achievement unlocked
                }
                else {
                    locked |= LockedListingFlags.Locked; // incomplete gate + inactive festival
                }
            }
            else {
                locked |= LockedListingFlags.AlwaysAvailableQuest;
            }
        }

        if (locked.HasFlag(LockedListingFlags.DeferredAchievement))
            return PieceResult.Deferred;
        if (locked.HasFlag(LockedListingFlags.FestivalActive) || locked.HasFlag(LockedListingFlags.AlwaysAvailableQuest))
            return PieceResult.Obtainable;
        return locked.HasFlag(LockedListingFlags.Locked) ? PieceResult.Unobtainable : PieceResult.Obtainable;
    }

    // items that themselves are not quest-gated, but are sold for seasonal currencies
    private static PieceResult? EvaluateSeasonalShopSources(uint itemId) {
        var acquisition = ItemAcquisitionService.Get();
        var sources = acquisition.GetSources(itemId);
        if (sources.Count == 0)
            return null;

        // ignore in this case or else you get stuff like mogtome dungeon gear flagged as unobtainable when the seasonal shop closes
        if (acquisition.HasDutyOrCraftSource(itemId))
            return null;

        var festivalGroups = sources.OfType<ItemSpecialShopSource>().Select(CollectSeasonalFestivalRefs).ToArray();
        // don't mark as unobtainable if any of the seasonal shop sources are empty
        if (festivalGroups.Any(f => f.Length == 0))
            return null;

        var seasonal = festivalGroups.Where(f => f.Length > 0).ToArray();
        if (seasonal.Length == 0)
            return null;

        return seasonal.Any(IsAnyFestivalActive) ? PieceResult.Obtainable : PieceResult.Unobtainable;
    }

    private static ImmutableArray<FestivalRef> CollectSeasonalFestivalRefs(ItemSpecialShopSource shopSrc) {
        var seen = new HashSet<(ushort, byte, byte)>();
        var result = new List<FestivalRef>();

        void Add(FestivalRef f) {
            if (f.FestivalId == 0 || !seen.Add((f.FestivalId, f.Begin, f.End)))
                return;
            result.Add(f);
        }

        foreach (var f in ResolveShopFestivalRefs(shopSrc.SpecialShop.Base))
            Add(f);

        foreach (var cost in shopSrc.CostItems) {
            if (cost.ItemId == 0)
                continue;
            foreach (var f in ResolveFestivalRefsFromCurrencyItem(cost.ItemId))
                Add(f);
        }

        return [.. result];
    }

    private static unsafe bool IsAnyFestivalActive(ImmutableArray<FestivalRef> refs) {
        if (refs.Length == 0)
            return false;

        var festivals = GameMain.Instance()->ActiveFestivals;
        foreach (var r in refs) {
            if (r.FestivalId == 0)
                continue;
            for (var i = 0; i < festivals.Length; i++) {
                var f = festivals[i];
                if (f.Id != r.FestivalId)
                    continue;
                // same accept window as quest festival gating: fail if phase < Begin or >= End
                if (f.Phase >= r.Begin && f.Phase < r.End)
                    return true;
            }
        }
        return false;
    }

    private static void IndexGilShop(uint shopId, Dictionary<uint, List<RepurchaseListing>> acc) {
        if (!Svc.Data.TryGetSubrows<GilShopItem>(shopId, out var rows))
            return;

        foreach (var row in rows) {
            var itemId = row.Item.RowId;
            if (itemId == 0)
                continue;

            var questIds = new List<uint>(2);
            foreach (var q in row.QuestRequired) {
                if (q.RowId != 0)
                    questIds.Add(q.RowId);
            }

            var achievementId = row.AchievementRequired.RowId;
            var festivals = ResolveFestivalRefs(questIds, achievementId);
            AddListing(acc, itemId, new RepurchaseListing {
                ShopKind = RepurchaseShopKind.GilShop,
                ShopId = shopId,
                QuestIds = [.. questIds],
                AchievementId = achievementId,
                FestivalRefs = festivals,
            });
        }
    }

    private static void IndexSpecialShop(uint shopId, Dictionary<uint, List<RepurchaseListing>> acc) {
        if (!SpecialShop.GetRowRef(shopId).IsValid)
            return;

        var shop = SpecialShop.GetRow(shopId);
        var shopFestivals = ResolveShopFestivalRefs(shop);
        foreach (var entry in shop.Item) {
            var questId = entry.Quest.RowId;
            var achievementId = entry.AchievementUnlock.RowId;
            ImmutableArray<uint> questIds;
            ImmutableArray<FestivalRef> festivals;

            if (questId == 0 && achievementId == 0) {
                // empty entry gates: still index when the shop/cost currency is seasonal
                // (skips true ungated currency-prize rows with no festival links)
                festivals = MergeFestivalRefs(shopFestivals, ResolveFestivalRefsFromEntryCosts(entry));
                if (festivals.Length == 0)
                    continue;
                questIds = [];
            }
            else {
                questIds = questId == 0 ? [] : [questId];
                festivals = MergeFestivalRefs(ResolveFestivalRefs(questIds, achievementId), shopFestivals);
            }

            foreach (var recv in entry.ReceiveItems) {
                var itemId = recv.Item.RowId;
                if (itemId == 0)
                    continue;
                AddListing(acc, itemId, new RepurchaseListing {
                    ShopKind = RepurchaseShopKind.SpecialShop,
                    ShopId = shopId,
                    QuestIds = questIds,
                    AchievementId = achievementId,
                    FestivalRefs = festivals,
                });
            }
        }
    }

    private static void AddListing(Dictionary<uint, List<RepurchaseListing>> acc, uint itemId, RepurchaseListing listing) {
        if (!acc.TryGetValue(itemId, out var list))
            acc[itemId] = list = [];
        // dedupe identical shop+gate rows shared across city NPCs
        if (list.Exists(x => x.ShopKind == listing.ShopKind && x.ShopId == listing.ShopId && x.AchievementId == listing.AchievementId && x.QuestIds.SequenceEqual(listing.QuestIds)))
            return;
        list.Add(listing);
    }

    private static ImmutableArray<FestivalRef> ResolveShopFestivalRefs(SpecialShop shop) {
        var festivalId = (ushort)shop.RequiredFestival.RowId;
        if (festivalId == 0)
            return [];
        var begin = shop.RequiredFestivalPhase > byte.MaxValue ? byte.MaxValue : (byte)shop.RequiredFestivalPhase;
        return [new FestivalRef(festivalId, begin, byte.MaxValue)];
    }

    private static ImmutableArray<FestivalRef> ResolveFestivalRefsFromEntryCosts(in SpecialShop.ItemStruct entry) {
        var questIds = new List<uint>();
        var seenCost = new HashSet<uint>();
        foreach (var cost in entry.ItemCosts) {
            var costId = cost.ItemCost.RowId;
            if (costId == 0 || !seenCost.Add(costId))
                continue;
            CollectQuestIdsFromCurrencyItem(costId, questIds);
        }
        return ResolveFestivalRefs(questIds, 0);
    }

    private static ImmutableArray<FestivalRef> ResolveFestivalRefsFromCurrencyItem(uint costItemId) {
        var questIds = new List<uint>();
        CollectQuestIdsFromCurrencyItem(costItemId, questIds);
        return ResolveFestivalRefs(questIds, 0);
    }

    private static void CollectQuestIdsFromCurrencyItem(uint costItemId, List<uint> questIds) {
        var seen = new HashSet<uint>(questIds);
        foreach (var src in ItemAcquisitionService.Get().GetSources(costItemId)) {
            if (src is not ItemQuestSource { Quest.RowId: var questId and not 0 })
                continue;
            if (seen.Add(questId))
                questIds.Add(questId);
        }
    }

    private static ImmutableArray<FestivalRef> MergeFestivalRefs(ImmutableArray<FestivalRef> a, ImmutableArray<FestivalRef> b) {
        if (a.Length == 0)
            return b;
        if (b.Length == 0)
            return a;
        var seen = new HashSet<(ushort, byte, byte)>();
        var result = new List<FestivalRef>(a.Length + b.Length);
        foreach (var f in a.Concat(b)) {
            if (f.FestivalId == 0 || !seen.Add((f.FestivalId, f.Begin, f.End)))
                continue;
            result.Add(f);
        }
        return [.. result];
    }

    private static ImmutableArray<FestivalRef> ResolveFestivalRefs(IReadOnlyList<uint> questIds, uint achievementId) {
        var seen = new HashSet<(ushort, byte, byte)>();
        var result = new List<FestivalRef>();

        void AddFromQuest(uint questId) {
            if (questId == 0 || !Quest.GetRowRef(questId).IsValid)
                return;
            var q = Quest.GetRow(questId);
            var festivalId = (ushort)q.Festival.RowId;
            if (festivalId == 0)
                return;
            var begin = q.FestivalBegin;
            var end = q.FestivalEnd;
            if (end == 0)
                end = byte.MaxValue;
            if (!seen.Add((festivalId, begin, end)))
                return;
            result.Add(new FestivalRef(festivalId, begin, end));
        }

        foreach (var questId in questIds)
            AddFromQuest(questId);

        // link achievement -> quest -> festival window
        if (achievementId != 0 && Achievement.GetRowRef(achievementId).IsValid) {
            var ach = Achievement.GetRow(achievementId);
            // type 6 / 9: Key + Data[] hold related quest ids. no link/festival -> treat achievement gate as obtainable
            if (ach.Type is 6 or 9) {
                if (ach.Key.RowId != 0)
                    AddFromQuest(ach.Key.RowId);
                foreach (var data in ach.Data) {
                    if (data.RowId != 0)
                        AddFromQuest(data.RowId);
                }
            }
        }

        return [.. result];
    }
}

file static class RepurchaseObtainabilityExtensions {
    public static bool ApplyRepurchase(this bool classificationUnobtainable, bool? repurchaseUnobtainable)
        => repurchaseUnobtainable ?? classificationUnobtainable;
}
