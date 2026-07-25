using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Data;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.VirtualFileSystem;
using FModel.FMDex;
using FModel.Framework;

namespace FModel.ViewModels;

public class SearchViewModel : ViewModel
{
    public enum ESortSizeMode
    {
        None,
        Ascending,
        Descending
    }

    private string _filterText = string.Empty;
    public string FilterText
    {
        get => _filterText;
        set => SetProperty(ref _filterText, value);
    }

    private string _tagFilterText = string.Empty;
    /// <summary>Class-tag filter against FMDex (e.g. Texture2D). Unindexed still appear at bottom.</summary>
    public string TagFilterText
    {
        get => _tagFilterText;
        set => SetProperty(ref _tagFilterText, value);
    }

    private bool _hasRegexEnabled;
    public bool HasRegexEnabled
    {
        get => _hasRegexEnabled;
        set => SetProperty(ref _hasRegexEnabled, value);
    }

    private bool _hasMatchCaseEnabled;
    public bool HasMatchCaseEnabled
    {
        get => _hasMatchCaseEnabled;
        set => SetProperty(ref _hasMatchCaseEnabled, value);
    }

    private ESortSizeMode _currentSortSizeMode = ESortSizeMode.None;
    public ESortSizeMode CurrentSortSizeMode
    {
        get => _currentSortSizeMode;
        set => SetProperty(ref _currentSortSizeMode, value);
    }

    private int _resultsCount;
    public int ResultsCount
    {
        get => _resultsCount;
        private set => SetProperty(ref _resultsCount, value);
    }

    private GameFile _refFile;
    public GameFile RefFile
    {
        get => _refFile;
        private set => SetProperty(ref _refFile, value);
    }

    public RangeObservableCollection<GameFile> SearchResults { get; }
    public ListCollectionView SearchResultsView { get; }

    public SearchViewModel()
    {
        SearchResults = [];
        SearchResultsView = new ListCollectionView(SearchResults)
        {
            Filter = ItemFilter,
            CustomSort = new FMDexSearchComparer(this),
        };
        ResultsCount = SearchResultsView.Count;
    }

    public void RefreshFilter()
    {
        FMDexService.Instance.EnsureLoaded();
        SearchResultsView.Refresh();
        ResultsCount = SearchResultsView.Count;
    }

    public void ChangeCollection(IEnumerable<GameFile> files, GameFile refFile = null)
    {
        FMDexService.Instance.EnsureLoaded();
        // Avoid O(n log n) CustomSort work while the bulk Reset lands — sort once after.
        var sort = SearchResultsView.CustomSort;
        SearchResultsView.CustomSort = null;
        SearchResults.Clear();
        SearchResults.AddRange(files);
        SearchResultsView.CustomSort = sort;
        RefFile = refFile;
        ResultsCount = SearchResultsView.Count;
    }

    public async Task CycleSortSizeMode()
    {
        CurrentSortSizeMode = CurrentSortSizeMode switch
        {
            ESortSizeMode.None => ESortSizeMode.Descending,
            ESortSizeMode.Descending => ESortSizeMode.Ascending,
            _ => ESortSizeMode.None
        };

        var sorted = await Task.Run(() =>
        {
            var archiveDict = SearchResults
                .OfType<VfsEntry>()
                .Select(f => f.Vfs.Name)
                .Distinct()
                .Select((name, idx) => (name, idx))
                .ToDictionary(x => x.name, x => x.idx);

            var keyed = SearchResults.Select(f =>
            {
                int archiveKey = f is VfsEntry ve && archiveDict.TryGetValue(ve.Vfs.Name, out var key) ? key : -1;
                return (File: f, f.Size, ArchiveKey: archiveKey);
            });

            return CurrentSortSizeMode switch
            {
                ESortSizeMode.Ascending => keyed
                    .OrderBy(x => x.Size).ThenBy(x => x.ArchiveKey)
                    .Select(x => x.File).ToList(),
                ESortSizeMode.Descending => keyed
                    .OrderByDescending(x => x.Size).ThenBy(x => x.ArchiveKey)
                    .Select(x => x.File).ToList(),
                _ => keyed
                    .OrderBy(x => x.ArchiveKey).ThenBy(x => x.File.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.File).ToList()
            };
        });

        SearchResults.Clear();
        SearchResults.AddRange(sorted);
        RefreshFilter();
    }

    private bool ItemFilter(object item)
    {
        if (item is not GameFile entry)
            return true;

        if (!PathMatches(entry))
            return false;

        var tagFilter = TagFilterText?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(tagFilter))
            return true;

        // map: .umap always; indexed map-related classes; skip other unindexed noise
        if (FMDexTagAliases.IsMapFilter(tagFilter))
        {
            if (FMDexTagAliases.IsUMap(entry))
                return true;
            if (!FMDexService.Instance.IsIndexed(entry))
                return false;
            return FMDexService.Instance.MatchesTagFilter(entry, tagFilter);
        }

        // Indexed (incl. extension defaults) with matching tag, OR true unindexed packages at bottom.
        if (!FMDexService.Instance.IsIndexed(entry))
            return true;

        return FMDexService.Instance.MatchesTagFilter(entry, tagFilter);
    }

    private bool PathMatches(GameFile entry)
    {
        var filterText = FilterText?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(filterText))
            return true;

        if (!HasRegexEnabled)
        {
            var filters = filterText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var cmp = HasMatchCaseEnabled ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return filters.All(token => TokenMatches(entry.Path, entry.Extension, token, cmp));
        }

        var o = RegexOptions.None;
        if (!HasMatchCaseEnabled) o |= RegexOptions.IgnoreCase;
        // Escape so ".uasset" means a literal extension, not "any char + uasset"
        var pattern = FilterText;
        if (pattern.StartsWith('.') && pattern.IndexOfAny(['*', '+', '?', '[', '(', '{', '|', '\\']) < 0)
            pattern = Regex.Escape(pattern);
        return new Regex(pattern, o).Match(entry.Path).Success
               || entry.Extension.Equals(FilterText.TrimStart('.'),
                   HasMatchCaseEnabled ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Match a search token against path, and treat ".uasset" / "uasset" as extension filters.
    /// </summary>
    internal static bool TokenMatches(string path, string extension, string token, StringComparison cmp)
    {
        if (string.IsNullOrEmpty(token))
            return true;
        if (!string.IsNullOrEmpty(path) && path.Contains(token, cmp))
            return true;

        var bare = token[0] == '.' ? token[1..] : token;
        if (bare.Length == 0)
            return false;

        // Extension-only token (no path separators / extra dots)
        if (bare.IndexOfAny(['/', '\\', '.']) >= 0)
            return false;

        if (!string.IsNullOrEmpty(extension) && extension.Equals(bare, cmp))
            return true;

        return !string.IsNullOrEmpty(path) && path.EndsWith("." + bare, cmp);
    }

    /// <summary>Indexed matches first; unindexed last, sorted by extension then path.
    /// For <c>map</c> filter: <c>.umap</c> first, then map-related classes.</summary>
    private sealed class FMDexSearchComparer(SearchViewModel owner) : IComparer
    {
        public int Compare(object x, object y)
        {
            if (x is not GameFile a || y is not GameFile b)
                return 0;

            // Size sort modes temporarily own ordering.
            if (owner.CurrentSortSizeMode != ESortSizeMode.None)
                return 0;

            var tagFilter = owner.TagFilterText?.Trim() ?? string.Empty;
            if (FMDexTagAliases.IsMapFilter(tagFilter))
            {
                var aUmap = FMDexTagAliases.IsUMap(a);
                var bUmap = FMDexTagAliases.IsUMap(b);
                if (aUmap != bUmap)
                    return aUmap ? -1 : 1;

                // Among non-umaps: indexed map classes before anything else
                if (!aUmap)
                {
                    var aIndexed = FMDexService.Instance.IsIndexed(a);
                    var bIndexed = FMDexService.Instance.IsIndexed(b);
                    if (aIndexed != bIndexed)
                        return aIndexed ? -1 : 1;
                }

                return string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            }

            var aIndexed2 = FMDexService.Instance.IsIndexed(a);
            var bIndexed2 = FMDexService.Instance.IsIndexed(b);
            if (aIndexed2 != bIndexed2)
                return aIndexed2 ? -1 : 1;

            if (!aIndexed2)
            {
                var ext = string.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase);
                if (ext != 0) return ext;
            }

            return string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        }
    }
}
