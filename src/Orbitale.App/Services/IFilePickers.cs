namespace Orbitale.App.Services;

/// <summary>
/// The file dialogs the view model needs, implemented by the window (pickers need its handle). Each opens in
/// <c>suggestedFolder</c> when it is given and exists (the folder remembered from the last session).
/// </summary>
public interface IFilePickers
{
    Task<string?> PickFolderAsync(string? suggestedFolder);

    Task<IReadOnlyList<string>> PickFramesAsync(string? suggestedFolder);

    /// <summary>Returns the chosen path, or null. <paramref name="format"/> is "tif" or "jpg" and selects the default type.</summary>
    Task<string?> PickSaveFileAsync(string suggestedName, string format, string? suggestedFolder);
}
