namespace TarkovCompanion.App.Services.Updates;

/// <summary>
/// Asked to apply a build when none is downloaded and verified: download it (again) first.
/// </summary>
/// <remarks>
/// #937: fetching the older build for "Go back" empties the updater's <c>packages\</c> folder, the
/// newer build's package with it, so "Update ready · Restart" has nothing left to apply.
/// </remarks>
public sealed class UpdateNotDownloadedException(string message) : InvalidOperationException(message);
