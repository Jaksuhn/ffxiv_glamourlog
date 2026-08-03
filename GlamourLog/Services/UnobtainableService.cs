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

    private bool _disposed;
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
        if (_disposed)
            return;
        _disposed = true;
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

    public bool HasGates => QuestIds.Length > 0 || AchievementId != 0;

    public unsafe bool IsUnlocked(bool achievementsLoaded, out bool deferredAchievement) {
        deferredAchievement = false;
        foreach (var questId in QuestIds) {
            if (!QuestManager.IsQuestComplete(questId))
                return false;
        }
        if (AchievementId == 0)
            return true;
        if (!achievementsLoaded) {
            deferredAchievement = true;
            return false;
        }
        return CSAchievement.Instance()->IsComplete((int)AchievementId);
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
        var sawListing = false;
        var deferred = false;
        var locked = false;
        var achievementsLoaded = CSAchievement.Instance()->IsLoaded();

        foreach (var itemId in itemIds) {
            if (!_byItemId.TryGetValue(itemId, out var listings) || listings.Length == 0)
                continue;

            sawListing = true;
            switch (EvaluatePiece(listings, achievementsLoaded)) {
                case PieceResult.Obtainable:
                    break;
                case PieceResult.Deferred:
                    deferred = true;
                    break;
                case PieceResult.Unobtainable:
                    locked = true;
                    break;
            }
        }

        if (!sawListing || deferred)
            return null;
        return locked;
    }

    private enum PieceResult : byte { Obtainable, Unobtainable, Deferred }

    private static PieceResult EvaluatePiece(ImmutableArray<RepurchaseListing> listings, bool achievementsLoaded) {
        var needsAchievement = false;
        var anyFestivalActive = false;
        var hasAlwaysAvailableQuest = false;
        var hasLockedSeasonalOrAchievement = false;

        foreach (var listing in listings) {
            if (!listing.HasGates)
                return PieceResult.Obtainable;

            if (listing.IsUnlocked(achievementsLoaded, out var deferredAch))
                return PieceResult.Obtainable;

            if (deferredAch)
                needsAchievement = true;

            if (listing.FestivalRefs.Length > 0) {
                if (IsAnyFestivalActive(listing.FestivalRefs))
                    anyFestivalActive = true;
                else
                    hasLockedSeasonalOrAchievement = true;
            }
            else if (listing.AchievementId != 0) {
                // achievements are loaded and they're incomplete
                if (!deferredAch)
                    hasLockedSeasonalOrAchievement = true;
            }
            else if (listing.QuestIds.Length > 0) {
                // festival == 0 should be non-seasonal quests
                hasAlwaysAvailableQuest = true;
            }
        }

        if (needsAchievement)
            return PieceResult.Deferred;
        if (anyFestivalActive || hasAlwaysAvailableQuest)
            return PieceResult.Obtainable;
        return hasLockedSeasonalOrAchievement ? PieceResult.Unobtainable : PieceResult.Obtainable;
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
        foreach (var entry in shop.Item) {
            var questId = entry.Quest.RowId;
            var achievementId = entry.AchievementUnlock.RowId;
            // empty SpecialShop gates (e.g. Seasonal Event currency prizes) must not drive obtainability
            if (questId == 0 && achievementId == 0)
                continue;

            var questIds = questId == 0 ? [] : ImmutableArray.Create(questId);
            var festivals = ResolveFestivalRefs(questIds, achievementId);

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

        if (achievementId != 0 && Achievement.GetRowRef(achievementId).IsValid) {
            var ach = Achievement.GetRow(achievementId);
            // type 6 / 9: Key + Data[] hold related quest ids
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
