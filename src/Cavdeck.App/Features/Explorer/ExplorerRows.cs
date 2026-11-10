using Cavdeck.App.Localization;
using Cavdeck.App.ViewModels;
using System.Windows.Media.Imaging;
using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Features.Explorer;

public sealed record ExplorerLocation(bool Buckets, string? Bucket, string Prefix);

public sealed class CrumbViewModel
{
    public CrumbViewModel(string label, bool isCurrent, bool showSeparator, ExplorerLocation location)
    {
        Label = label;
        IsCurrent = isCurrent;
        ShowSeparator = showSeparator;
        Location = location;
    }

    public string Label { get; }
    public bool IsCurrent { get; }
    public bool ShowSeparator { get; }
    public bool IsLink => !IsCurrent;
    public ExplorerLocation Location { get; }
}

public sealed class ExplorerRow : ViewModelBase
{
    private readonly LocalizationService _texts;
    private readonly string? _displayName;

    public ExplorerRow(ObjectEntry entry, LocalizationService texts, string? displayName = null)
    {
        Entry = entry;
        _texts = texts;
        _displayName = displayName;
    }

    private bool _isDropTarget;
    private BitmapSource? _thumbnail;
    public BitmapSource? Thumbnail => _thumbnail;
    public bool HasThumbnail => _thumbnail is not null;
    public bool ThumbnailCandidate => ThumbnailPipeline.IsEligible(Entry);

    public void SetThumbnail(BitmapSource? image)
    {
        if (ReferenceEquals(_thumbnail, image))
            return;
        _thumbnail = image;
        OnPropertyChanged(nameof(Thumbnail));
        OnPropertyChanged(nameof(HasThumbnail));
    }

    public bool IsDropTarget => _isDropTarget;
    public void SetDropTarget(bool active) =>
        SetProperty(ref _isDropTarget, active, nameof(IsDropTarget));

    public ObjectEntry Entry { get; private set; }

    public void Replace(ObjectEntry entry)
    {
        if (!Equals(Entry, entry))
            SetThumbnail(null);
        Entry = entry;
        OnPropertyChanged(nameof(ThumbnailCandidate));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(SizeBytes));
        OnPropertyChanged(nameof(Size));
        OnPropertyChanged(nameof(Modified));
        OnPropertyChanged(nameof(ModifiedText));
        OnPropertyChanged(nameof(FileKind));
        OnPropertyChanged(nameof(IconKind));
        OnPropertyChanged(nameof(Kind));
    }
    public string Name => Entry.Name;
    public string DisplayName => _displayName ?? Entry.Name;
    public long SizeBytes => Entry.SizeBytes;
    public DateTimeOffset? Modified => Entry.LastModified;
    public bool IsFolder => Entry.Kind == ObjectEntryKind.Folder;
    public ObjectFileKind FileKind => ObjectFileKinds.Classify(Name, IsFolder);
    public FileIconKind IconKind => FileIconKinds.Classify(Name, IsFolder);
    public string Size => ExplorerText.Size(Entry);
    public string ModifiedText => ExplorerText.Modified(Entry);
    public string Kind => _texts[ExplorerText.KindKey(FileKind)];

    public void Refresh()
    {
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(ModifiedText));
        OnPropertyChanged(nameof(Size));
    }
}

public sealed class BucketRow : ViewModelBase
{
    private readonly LocalizationService _texts;
    private bool _isFavorite;

    public BucketRow(StorageBucket bucket, LocalizationService texts, bool isFavorite = false)
    {
        Bucket = bucket;
        _texts = texts;
        _isFavorite = isFavorite;
    }

    public StorageBucket Bucket { get; }
    public string Name => Bucket.Name;
    public string Region => string.IsNullOrWhiteSpace(Bucket.Region) ? "—" : Bucket.Region!;
    public bool IsFavorite => _isFavorite;
    public string FavoriteActionText => _texts[IsFavorite ? "Places_RemoveFavorite" : "Places_AddFavorite"];

    public void SetFavorite(bool value)
    {
        if (SetProperty(ref _isFavorite, value, nameof(IsFavorite)))
            OnPropertyChanged(nameof(FavoriteActionText));
    }

    public void RefreshLanguage() => OnPropertyChanged(nameof(FavoriteActionText));
}

public enum ExplorerDetailScope { Object, Location, Selection, Connection }

public sealed class ExplorerDetail
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required string Size { get; init; }
    public required string Modified { get; init; }
    public required string Key { get; init; }
    public required string ETag { get; init; }
    public required string ETagRaw { get; init; }
    public required string Uri { get; init; }
    public required string ContentType { get; init; }
    public required string ContentTypeDisplay { get; init; }
    public required string SizeTooltip { get; init; }
    public required string ModifiedTooltip { get; init; }
    public required string KeyLabel { get; init; }
    public required string ModifiedSummary { get; init; }
    public bool IsFolder { get; init; }
    public string Bucket { get; init; } = "";
    public ExplorerDetailScope Scope { get; init; } = ExplorerDetailScope.Object;
    public bool ShowFileFacts => Scope == ExplorerDetailScope.Object && !IsFolder;
    public bool ShowLocation => Scope is ExplorerDetailScope.Object or ExplorerDetailScope.Location;
    public bool ShowBucket => Scope == ExplorerDetailScope.Location && Bucket.Length > 0;
    public bool ShowKey => ShowLocation && Key.Length > 0;
    public bool ShowUri => ShowLocation && Uri.Length > 0;
    public bool ShowNameCopy => Scope != ExplorerDetailScope.Selection && Name.Length > 0;
    public string Summary => IsFolder ? Kind : $"{Kind} · {Size}";
    public bool ShowHeaders { get; init; }
    public bool ShowEtag => ETag.Length > 0;
}

public static class ExplorerContextDetailFactory
{
    // S3 folders are prefixes, not real directories: do not invent dates, sizes, or item totals.
    public static ExplorerDetail ForCurrentLocation(
        StorageConnection connection, string? bucket, string prefix, LocalizationService texts)
    {
        var name = bucket is null ? connection.Name
            : prefix.Length == 0 ? bucket : ObjectLayout.Crumbs(prefix)[^1].Name;
        var kind = bucket is null ? texts["Places_Connection"]
            : prefix.Length == 0 ? texts["Explorer_DetailsBucket"] : texts["Explorer_DetailsCurrentFolder"];
        return Create(name, kind, bucket ?? "", prefix,
            bucket is null ? "" : ObjectLayout.ObjectUri(bucket, prefix),
            bucket is null ? ExplorerDetailScope.Connection : ExplorerDetailScope.Location, texts);
    }

    public static ExplorerDetail ForSelection(IReadOnlyList<ExplorerRow> rows, LocalizationService texts)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count < 2)
            throw new ArgumentException("Selection summary requires multiple items.", nameof(rows));

        var folders = rows.Count(row => row.IsFolder);
        var summary = string.Format(texts["Explorer_DetailsSelectionBreakdown"],
            rows.Count - folders, folders);
        return Create(string.Format(texts["Explorer_StatusSelected"], rows.Count), summary,
            "", "", "", ExplorerDetailScope.Selection, texts);
    }

    private static ExplorerDetail Create(
        string name, string kind, string bucket, string key, string uri,
        ExplorerDetailScope scope, LocalizationService texts) => new()
    {
        Name = name,
        Kind = kind,
        Size = "",
        Modified = "",
        Key = key,
        ETag = "",
        ETagRaw = "",
        Uri = uri,
        ContentType = "",
        ContentTypeDisplay = "",
        SizeTooltip = "",
        ModifiedTooltip = "",
        KeyLabel = texts["Explorer_Prefix"],
        ModifiedSummary = "",
        IsFolder = true,
        ShowHeaders = false,
        Bucket = bucket,
        Scope = scope
    };
}

