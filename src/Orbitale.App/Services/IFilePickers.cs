namespace Orbitale.App.Services;

/// <summary>The file dialogs the view model needs, implemented by the window (pickers need its handle).</summary>
public interface IFilePickers
{
    Task<string?> PickFolderAsync();

    Task<IReadOnlyList<string>> PickFramesAsync();

    /// <summary>Returns the chosen path, or null. <paramref name="format"/> is "tif" or "jpg" and selects the default type.</summary>
    Task<string?> PickSaveFileAsync(string suggestedName, string format);
}
