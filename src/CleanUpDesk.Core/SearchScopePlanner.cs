namespace CleanUpDesk.Core;
public static class SearchScopePlanner
{
    /// <summary>Builds explicit folder or volume refresh scopes using connected stable identities and the requested recursion mode.</summary>
    public static IReadOnlyList<SearchRefreshRoot> Plan(SearchFilter filter, IEnumerable<Volume> volumes)
    {
        var live = volumes.Where(v => v.Available && (filter.Volumes.Length == 0 || filter.Volumes.Contains(v.Id))).ToArray();
        if (filter.Folder.Length > 0)
        {
            var folder = Paths.Normalize(filter.Folder); var v = live.FirstOrDefault(v => Paths.Within(folder, v.Root));
            return v == null ? [] : [new(folder, v.Id, filter.Recursive)];
        }
        return SearchLocations.Collapse(live.Select(v => new SearchRefreshRoot(v.Root, v.Id)));
    }
}
