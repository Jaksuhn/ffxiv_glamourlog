namespace GlamourLog.Windows.GuideWindow;

internal interface IGuidePage {
    string Id { get; }
    GuideCategory Category { get; }
    int Order { get; }
    string Title { get; }

    IReadOnlyList<IGuideBlock> BuildBlocks(GuidePageContext context);
}

internal enum GuideCategory {
    Guide,
    Tweaks,
    Export,
    Settings,
    Debug,
}

internal static class GuideCategoryExtensions {
    internal static string Title(this GuideCategory category)
        => category switch {
            GuideCategory.Guide => "Guide",
            GuideCategory.Tweaks => "Tweaks",
            GuideCategory.Export => "Export",
            GuideCategory.Settings => "Settings",
            GuideCategory.Debug => "Debug",
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
        };

    internal static int Order(this GuideCategory category)
        => category switch {
            GuideCategory.Guide => 0,
            GuideCategory.Tweaks => 1,
            GuideCategory.Export => 2,
            GuideCategory.Settings => 3,
            GuideCategory.Debug => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
        };
}

internal sealed class GuidePageContext {
    internal Configuration Configuration => C;
    internal Services.OwnershipService Ownership => Services.OwnershipService.Get();
    internal Services.WindowsService Windows => Services.WindowsService.Get();
}

internal sealed record GuideCategoryPages(string Title, IReadOnlyList<IGuidePage> Pages);

internal static class GuidePageCatalog {
#if DEBUG
    private const bool IncludeDebugPages = true;
#else
    private const bool IncludeDebugPages = false;
#endif

    internal static IReadOnlyList<GuideCategoryPages> Categories { get; } = Discover();

    private static IReadOnlyList<GuideCategoryPages> Discover() {
        var pages = typeof(IGuidePage).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && !type.IsInterface && typeof(IGuidePage).IsAssignableFrom(type))
            .Select(type => (IGuidePage?)Activator.CreateInstance(type, nonPublic: true) ?? throw new InvalidOperationException($"Could not create guide page {type.FullName}."))
            .Where(page => IncludeDebugPages || page.Category != GuideCategory.Debug)
            .ToArray();

        var duplicateIds = pages.GroupBy(page => page.Id, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        if (duplicateIds.Length > 0)
            throw new InvalidOperationException($"Duplicate guide page IDs: {string.Join(", ", duplicateIds)}");

        if (pages.Length == 0)
            throw new InvalidOperationException("No guide pages were discovered.");

        var invalidPage = pages.FirstOrDefault(page => string.IsNullOrWhiteSpace(page.Id) || string.IsNullOrWhiteSpace(page.Title));
        if (invalidPage is not null)
            throw new InvalidOperationException($"{invalidPage.GetType().FullName} has an empty guide page ID or title.");

        var duplicateOrders = pages.GroupBy(page => (page.Category, page.Order)).Where(group => group.Count() > 1).Select(group => $"{group.Key.Category}:{group.Key.Order}").ToArray();
        if (duplicateOrders.Length > 0)
            throw new InvalidOperationException($"Duplicate guide page category/order values: {string.Join(", ", duplicateOrders)}");

        return [.. pages
            .GroupBy(page => page.Category)
            .OrderBy(group => group.Key.Order())
            .Select(group => new GuideCategoryPages(group.Key.Title(),[.. group.OrderBy(page => page.Order).ThenBy(page => page.Id, StringComparer.Ordinal)]))];
    }
}
