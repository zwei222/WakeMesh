using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WakeMesh.Mobile.Models;

namespace WakeMesh.Mobile.Services;

/// <summary>Lifecycle of the last auth settings load.</summary>
public enum AuthSettingsLoadStatus
{
    NotLoaded = 0,
    Loaded = 1,
    Missing = 2,
    Corrupted = 3,
}

/// <param name="Status">Outcome of the last load.</param>
/// <param name="Code">Stable, non-secret reason code (for example <c>invalidJson</c>).</param>
/// <param name="Detail">Non-secret detail: file name and/or exception type only. Never file contents.</param>
public sealed record AuthSettingsLoadDiagnostic(
    AuthSettingsLoadStatus Status,
    string Code,
    string? Detail = null)
{
    /// <summary>True when the caller must treat the persisted settings as untrusted.</summary>
    public bool HasWarning => Status == AuthSettingsLoadStatus.Corrupted;
}

/// <summary>
/// Typed failure for unreadable/corrupt auth settings documents. Deliberately carries only the file
/// name, a reason code and the exception type — never the document body (no secret material).
/// </summary>
public sealed class CorruptedAuthSettingsException : Exception
{
    public string FileName { get; }
    public string Code { get; }

    public CorruptedAuthSettingsException(string fileName, string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        FileName = fileName;
        Code = code;
    }
}

/// <summary>
/// Durable JSON store for <see cref="AuthSettings"/>.
/// Writes are atomic (temp file + flush-to-disk + rename) under a full-path exclusive gate, so a crash
/// mid-write cannot leave a truncated document and concurrent store instances over the same path
/// cannot interleave. A corrupt document is reported as a typed
/// <see cref="CorruptedAuthSettingsException"/> plus <see cref="LastLoadDiagnostic"/>; it is never
/// silently downgraded to "unconfigured" and never overwritten by a home-account-only save.
/// </summary>
public sealed class JsonAuthSettingsStore : IAuthSettingsStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private string? _selectedHomeAccountId;
    private AuthSettingsLoadDiagnostic _lastLoadDiagnostic = new(AuthSettingsLoadStatus.NotLoaded, "notLoaded");

    private sealed class AuthSettingsDocument
    {
        public string? ApiBaseUrl { get; set; }
        public string? AndroidClientId { get; set; }
        public string? ApiClientId { get; set; }
        public string? SelectedHomeAccountId { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

    public JsonAuthSettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? AlarmStoragePathHelper.GetAuthSettingsFilePath();
    }

    public string FilePath => _filePath;

    /// <summary>
    /// Last loaded/saved MSAL home account id bound to these settings. Never logs the value.
    /// </summary>
    public string? SelectedHomeAccountId => _selectedHomeAccountId;

    /// <summary>Diagnostic for the most recent load, including typed corruption warnings.</summary>
    public AuthSettingsLoadDiagnostic LastLoadDiagnostic => _lastLoadDiagnostic;

    private SemaphoreSlim GetGate()
        => PathGates.GetOrAdd(Path.GetFullPath(_filePath), static _ => new SemaphoreSlim(1, 1));

    public async Task<AuthSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        var gate = GetGate();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
            {
                _selectedHomeAccountId = null;
                _lastLoadDiagnostic = new AuthSettingsLoadDiagnostic(AuthSettingsLoadStatus.Missing, "missingFile");
                return new AuthSettings();
            }

            string json;
            try
            {
                json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw Corrupted("ioReadFailed", ex);
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                throw Corrupted("emptyDocument", null);
            }

            AuthSettingsDocument? doc;
            try
            {
                doc = JsonSerializer.Deserialize<AuthSettingsDocument>(json);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw Corrupted("invalidJson", ex);
            }

            if (doc == null)
            {
                throw Corrupted("nullDocument", null);
            }

            _selectedHomeAccountId = string.IsNullOrWhiteSpace(doc.SelectedHomeAccountId)
                ? null
                : doc.SelectedHomeAccountId.Trim();
            _lastLoadDiagnostic = new AuthSettingsLoadDiagnostic(AuthSettingsLoadStatus.Loaded, "loaded");

            return new AuthSettings
            {
                ApiBaseUrl = doc.ApiBaseUrl,
                AndroidClientId = doc.AndroidClientId,
                ApiClientId = doc.ApiClientId,
            };
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveSettingsAsync(AuthSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await SaveCoreAsync(settings, _selectedHomeAccountId, cancellationToken).ConfigureAwait(false);
    }

    public Task SaveSelectedHomeAccountIdAsync(string? homeAccountId, CancellationToken cancellationToken = default)
        => SaveCoreAsync(settings: null, homeAccountId, cancellationToken);

    private async Task SaveCoreAsync(AuthSettings? settings, string? homeAccountId, CancellationToken cancellationToken)
    {
        var gate = GetGate();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AuthSettings effectiveSettings;
            if (settings != null)
            {
                effectiveSettings = settings;
            }
            else if (File.Exists(_filePath))
            {
                // Preserve the persisted URL/client ids; a corrupt document must fail loudly instead of
                // being replaced by an empty one.
                var jsonExisting = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
                AuthSettingsDocument? existing;
                try
                {
                    existing = JsonSerializer.Deserialize<AuthSettingsDocument>(jsonExisting);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    throw Corrupted("invalidJson", ex);
                }

                if (existing == null)
                {
                    throw Corrupted("nullDocument", null);
                }

                effectiveSettings = new AuthSettings
                {
                    ApiBaseUrl = existing.ApiBaseUrl,
                    AndroidClientId = existing.AndroidClientId,
                    ApiClientId = existing.ApiClientId,
                };
            }
            else
            {
                effectiveSettings = new AuthSettings();
            }

            _selectedHomeAccountId = string.IsNullOrWhiteSpace(homeAccountId) ? null : homeAccountId.Trim();

            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var doc = new AuthSettingsDocument
            {
                ApiBaseUrl = effectiveSettings.ApiBaseUrl,
                AndroidClientId = effectiveSettings.AndroidClientId,
                ApiClientId = effectiveSettings.ApiClientId,
                SelectedHomeAccountId = _selectedHomeAccountId,
            };

            var json = JsonSerializer.Serialize(doc, JsonOptions);
            var tempPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, Encoding.UTF8))
                {
                    await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, _filePath, overwrite: true);
            }
            catch
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // Best effort temp cleanup; the original document is untouched.
                }

                throw;
            }

            _lastLoadDiagnostic = new AuthSettingsLoadDiagnostic(
                AuthSettingsLoadStatus.Loaded,
                "saved",
                Path.GetFileName(_filePath));
        }
        finally
        {
            gate.Release();
        }
    }

    private CorruptedAuthSettingsException Corrupted(string code, Exception? inner)
    {
        var fileName = Path.GetFileName(_filePath);
        _lastLoadDiagnostic = new AuthSettingsLoadDiagnostic(
            AuthSettingsLoadStatus.Corrupted,
            code,
            inner is null ? fileName : $"{fileName}:{inner.GetType().Name}");

        // No document contents and no exception message are surfaced (secret-free diagnostics).
        return new CorruptedAuthSettingsException(
            fileName,
            code,
            $"認証設定ファイル '{fileName}' を読み込めませんでした (code={code})。破損として扱い、未設定の成功扱いにはしません。",
            inner);
    }
}
