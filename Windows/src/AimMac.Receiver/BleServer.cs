using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using AimMac.Core.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace AimMac.Receiver;

internal sealed class BleWriteEventArgs : EventArgs
{
    public required string ConnectionId { get; init; }
    public required string? OsDeviceId { get; init; }
    public required byte[] Payload { get; init; }
}

/// <summary>
/// GATT server for the AimMac service. Encryption is a property of the
/// published characteristics, not of the hello packet. Whether Windows actually
/// rejects unencrypted writes has not been tested on hardware.
/// </summary>
internal sealed class BleServer
{
    private readonly Dictionary<string, GattSubscribedClient> _clients = new(StringComparer.Ordinal);
    private GattServiceProvider? _provider;
    private GattLocalCharacteristic? _input;
    private GattLocalCharacteristic? _status;
    private Func<string, byte[]>? _statusFor;
    private int _anonymous;

    public bool RequiresEncryption { get; private set; }
    public bool Advertising { get; private set; }
    public string Summary { get; private set; } = "Bluetooth has not been started.";

    public event EventHandler<string>? Subscribed;
    public event EventHandler<string>? Unsubscribed;
    public event EventHandler<BleWriteEventArgs>? Written;
    public event EventHandler<string>? Faulted;

    public async Task<bool> StartAsync(bool requireEncryption, Func<string, byte[]> statusFor)
    {
        Stop();
        _statusFor = statusFor;
        RequiresEncryption = requireEncryption;
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null)
            {
                Summary = "No Bluetooth adapter. Cursor control stays off.";
                return false;
            }

            Summary = Describe(adapter);
            if (!adapter.IsLowEnergySupported || !adapter.IsPeripheralRoleSupported)
            {
                Summary += " Peripheral role is not available, so AimMac will not advertise.";
                return false;
            }

            var created = await GattServiceProvider.CreateAsync(ProtocolIds.Service);
            if (created.Error != BluetoothError.Success || created.ServiceProvider is null)
            {
                Summary += " GATT service was not created (" + created.Error + ").";
                return false;
            }

            _provider = created.ServiceProvider;
            var level = requireEncryption
                ? GattProtectionLevel.EncryptionAndAuthenticationRequired
                : GattProtectionLevel.Plain;
            var inputResult = await _provider.Service.CreateCharacteristicAsync(
                ProtocolIds.Input,
                new GattLocalCharacteristicParameters
                {
                    CharacteristicProperties = GattCharacteristicProperties.WriteWithoutResponse,
                    WriteProtectionLevel = level,
                });
            if (inputResult.Error != BluetoothError.Success || inputResult.Characteristic is null)
            {
                Summary += " Input characteristic was not created (" + inputResult.Error + ").";
                Stop();
                return false;
            }

            var statusResult = await _provider.Service.CreateCharacteristicAsync(
                ProtocolIds.Status,
                new GattLocalCharacteristicParameters
                {
                    CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
                    ReadProtectionLevel = level,
                });
            if (statusResult.Error != BluetoothError.Success || statusResult.Characteristic is null)
            {
                Summary += " Status characteristic was not created (" + statusResult.Error + ").";
                Stop();
                return false;
            }

            _input = inputResult.Characteristic;
            _status = statusResult.Characteristic;
            _input.WriteRequested += OnWriteRequested;
            _status.ReadRequested += OnReadRequested;
            _status.SubscribedClientsChanged += OnSubscribersChanged;
            _provider.StartAdvertising(new GattServiceProviderAdvertisingParameters
            {
                IsConnectable = true,
                IsDiscoverable = true,
            });
            Advertising = true;
            Summary += requireEncryption
                ? " Advertising with authenticated encryption required."
                : " Advertising in plaintext prototype mode.";
            return true;
        }
        catch (Exception ex)
        {
            Summary = "Bluetooth start failed: " + Trim(ex.Message) + " Cursor control stays off.";
            Stop();
            return false;
        }
    }

    public void Notify(string connectionId)
    {
        if (_status is null || _statusFor is null || !_clients.TryGetValue(connectionId, out var client))
        {
            return;
        }

        try
        {
            var bytes = _statusFor(connectionId);
            var buffer = bytes.AsBuffer();
            _ = _status.NotifyValueAsync(buffer, client);
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(this, Trim(ex.Message));
        }
    }

    public void Stop()
    {
        Advertising = false;
        try
        {
            _provider?.StopAdvertising();
        }
        catch (Exception ex)
        {
            Summary = "Bluetooth stop failed: " + Trim(ex.Message);
        }

        if (_input is not null)
        {
            _input.WriteRequested -= OnWriteRequested;
        }

        if (_status is not null)
        {
            _status.ReadRequested -= OnReadRequested;
            _status.SubscribedClientsChanged -= OnSubscribersChanged;
        }

        _clients.Clear();
        _input = null;
        _status = null;
        _provider = null;
        _statusFor = null;
    }

    private async void OnWriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync();
            if (request is null)
            {
                return;
            }

            byte[] payload = [];
            if (request.Offset == 0 && request.Value is not null)
            {
                var reader = DataReader.FromBuffer(request.Value);
                payload = new byte[reader.UnconsumedBufferLength];
                reader.ReadBytes(payload);
            }

            if (request.Option == GattWriteOption.WriteWithResponse)
            {
                request.Respond();
            }

            var (connectionId, osId) = Identity(args.Session);
            Written?.Invoke(this, new BleWriteEventArgs
            {
                ConnectionId = connectionId,
                OsDeviceId = osId,
                Payload = payload,
            });
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(this, Trim(ex.Message));
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnReadRequested(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync();
            if (request is null || _statusFor is null)
            {
                return;
            }

            var (connectionId, _) = Identity(args.Session);
            var bytes = _statusFor(connectionId);
            if (request.Offset != 0)
            {
                request.RespondWithProtocolError(GattProtocolError.InvalidOffset);
                return;
            }

            request.RespondWithValue(bytes.AsBuffer());
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(this, Trim(ex.Message));
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnSubscribersChanged(GattLocalCharacteristic sender, object args)
    {
        try
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var client in sender.SubscribedClients)
            {
                var (connectionId, _) = Identity(client.Session);
                seen.Add(connectionId);
                if (_clients.ContainsKey(connectionId))
                {
                    continue;
                }

                _clients[connectionId] = client;
                Subscribed?.Invoke(this, connectionId);
            }

            foreach (var gone in _clients.Keys.Where(id => !seen.Contains(id)).ToArray())
            {
                _clients.Remove(gone);
                Unsubscribed?.Invoke(this, gone);
            }
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(this, Trim(ex.Message));
        }
    }

    private (string ConnectionId, string? OsDeviceId) Identity(GattSession? session)
    {
        var osId = session?.DeviceId?.Id;
        if (!string.IsNullOrWhiteSpace(osId))
        {
            return (osId, osId);
        }

        var anon = "anon-" + (session is null ? Interlocked.Increment(ref _anonymous) : RuntimeHelpers.GetHashCode(session));
        return (anon, null);
    }

    private static string Describe(BluetoothAdapter adapter)
    {
        return "Adapter BLE " + Yes(adapter.IsLowEnergySupported)
            + ", peripheral " + Yes(adapter.IsPeripheralRoleSupported)
            + ", central " + Yes(adapter.IsCentralRoleSupported)
            + ", LE secure connections " + Yes(adapter.AreLowEnergySecureConnectionsSupported)
            + ", advertisement offload " + Yes(adapter.IsAdvertisementOffloadSupported)
            + ", max advertisement " + adapter.MaxAdvertisementDataLength + " bytes.";
    }

    private static string Yes(bool value) => value ? "yes" : "no";

    private static string Trim(string message)
    {
        var text = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 180 ? text : text[..180];
    }
}
