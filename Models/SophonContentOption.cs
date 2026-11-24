namespace KitsuneSophonDownloader.Models;

public sealed class SophonContentOption
{
    private string? _translatedName;

    public ManifestCategory Category { get; }

    public string OriginalName =>
        string.IsNullOrWhiteSpace(Category.category_name)
            ? Category.category_id
            : Category.category_name;

    public string Name =>
        string.IsNullOrWhiteSpace(_translatedName)
            ? OriginalName
            : _translatedName;

    // Display-only translation. API identifiers and the original category name remain unchanged.
    public string? TranslatedName
    {
        get => _translatedName;
        set => _translatedName = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public bool IsSelected { get; set; }

    public SophonContentOption(ManifestCategory category)
    {
        Category = category ?? throw new ArgumentNullException(nameof(category));
        IsSelected = false;
    }
}
