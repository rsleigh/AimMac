using System.Text.Json;

namespace AimMac.Core.Session;

/// <summary>
/// Persists OS Bluetooth device ids for secure mode only.
/// A corrupt file is ignored so a broken store cannot approve anyone.
/// </summary>
public sealed class JsonApprovalStore : IApprovalStore
{
    private readonly string _path;
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    public JsonApprovalStore(string path)
    {
        _path = path;
        Load();
    }

    public bool Contains(string osDeviceId) => _ids.Contains(osDeviceId);

    public void Add(string osDeviceId)
    {
        if (!MemoryApprovalStore.Accept(osDeviceId))
        {
            return;
        }

        if (_ids.Add(osDeviceId))
        {
            Save();
        }
    }

    public bool Remove(string osDeviceId)
    {
        if (!_ids.Remove(osDeviceId))
        {
            return false;
        }

        Save();
        return true;
    }

    public IReadOnlyCollection<string> All => _ids.ToArray();

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var json = File.ReadAllText(_path);
            var doc = JsonSerializer.Deserialize<ApprovalFile>(json);
            if (doc?.ApprovedOsDeviceIds is null)
            {
                return;
            }

            foreach (var id in doc.ApprovedOsDeviceIds)
            {
                if (MemoryApprovalStore.Accept(id))
                {
                    _ids.Add(id);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _ids.Clear();
        }
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = new ApprovalFile { ApprovedOsDeviceIds = _ids.Order(StringComparer.Ordinal).ToArray() };
        var json = JsonSerializer.Serialize(payload);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, _path, overwrite: true);
    }

    private sealed class ApprovalFile
    {
        public string[]? ApprovedOsDeviceIds { get; set; }
    }
}
