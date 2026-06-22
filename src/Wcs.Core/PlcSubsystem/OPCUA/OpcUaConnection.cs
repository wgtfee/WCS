using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using Wcs.Core.PlcSubsystem.Abstractions;

namespace Wcs.Core.PlcSubsystem.OpcUa;

/// <summary>
/// OPC UA 连接配置
/// </summary>
public class OpcUaConnectionConfig
{
    public string Name { get; set; } = "OpcUaServer";
    public string EndpointUrl { get; set; } = "opc.tcp://localhost:4840";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool UseSecurity { get; set; }
    public int TimeoutMs { get; set; } = 10000;
    public int ReconnectIntervalMs { get; set; } = 5000;
}

/// <summary>
/// OPC UA 连接实现 — 基于 OPC Foundation 官方库
/// </summary>
public class OpcUaConnection : PlcConnectionBase
{
    private readonly OpcUaConnectionConfig _config;
    private Session? _session;
    private SessionReconnectHandler? _reconnectHandler;
    private ApplicationConfiguration? _appConfig;

    public override PlcProtocolType ProtocolType => PlcProtocolType.OpcUa;
    public Session? Session => _session;

    public OpcUaConnection(OpcUaConnectionConfig config, ILogger<OpcUaConnection> logger)
        : base(config.Name, logger)
    {
        _config = config;
        Status.ProtocolType = ProtocolType;
    }

    public override async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        if (Connected) return true;
        SetStatus(PlcConnectionStatusEnum.Connecting);

        try
        {
            _appConfig = await CreateApplicationConfig();
            await _appConfig.Validate(ApplicationType.Client);

            // 手动构建端点
            var endpointUrl = new Uri(_config.EndpointUrl);
            var selectedEndpoint = new EndpointDescription
            {
                EndpointUrl = endpointUrl.OriginalString,
                Server = new ApplicationDescription { ApplicationUri = $"urn:{Name}" },
                SecurityMode = _config.UseSecurity ? MessageSecurityMode.SignAndEncrypt : MessageSecurityMode.None,
                SecurityPolicyUri = _config.UseSecurity ? SecurityPolicies.Basic256Sha256 : SecurityPolicies.None,
            };
            var endpointConfig = EndpointConfiguration.Create(_appConfig);

            var identity = string.IsNullOrEmpty(_config.Username)
                ? new UserIdentity(new AnonymousIdentityToken())
                : new UserIdentity(_config.Username, System.Text.Encoding.UTF8.GetBytes(_config.Password ?? string.Empty));

            var configuredEndpoint = new ConfiguredEndpoint(null, selectedEndpoint, endpointConfig);

            _session = await Session.Create(
                _appConfig,
                configuredEndpoint,
                true,
                false,
                $"{Name}Session",
                60000,
                identity,
                null
            );

            _session.KeepAlive += OnKeepAlive;

            Connected = true;
            SetStatus(PlcConnectionStatusEnum.Connected);
            Logger.LogInformation("OPC UA [{Name}] connected to {Url}",
                Name, _config.EndpointUrl);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus(PlcConnectionStatusEnum.Failed, ex.Message);
            Logger.LogError(ex, "OPC UA [{Name}] connect failed", Name);
            return false;
        }
    }

    public override async Task<bool> DisconnectAsync(CancellationToken ct = default)
    {
        if (!Connected) return true;
        SetStatus(PlcConnectionStatusEnum.Disconnecting);

        try
        {
            _reconnectHandler?.Dispose();
            _session?.CloseAsync().GetAwaiter().GetResult();
            _session?.Dispose();
            Connected = false;
            SetStatus(PlcConnectionStatusEnum.Disconnected);
            Logger.LogInformation("OPC UA [{Name}] disconnected", Name);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus(PlcConnectionStatusEnum.Failed, ex.Message);
            return false;
        }
    }

    public override async Task<byte[]?> ReadAsync(string address, ushort length, CancellationToken ct = default)
    {
        if (_session == null || !Connected) return null;

        try
        {
            var nodeId = new NodeId(address);
            var readValue = new ReadValueId
            {
                NodeId = nodeId,
                AttributeId = Attributes.Value,
                Handle = 0
            };

            var response = await _session.ReadAsync(
                null, 0, TimestampsToReturn.Neither, [readValue], ct);

            if (response?.Results == null || response.Results.Count == 0)
                return null;

            var value = response.Results[0].Value;
            CountRead();
            return value switch
            {
                byte[] bytes => bytes,
                int i => BitConverter.GetBytes(i),
                uint u => BitConverter.GetBytes(u),
                short s => BitConverter.GetBytes(s),
                ushort us => BitConverter.GetBytes(us),
                long l => BitConverter.GetBytes(l),
                float f => BitConverter.GetBytes(f),
                double d => BitConverter.GetBytes(d),
                bool b => new[] { (byte)(b ? 1 : 0) },
                string s => System.Text.Encoding.UTF8.GetBytes(s),
                _ => null
            };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "OPC UA [{Name}] read {Addr} failed", Name, address);
            return null;
        }
    }

    public override async Task<bool> WriteAsync(string address, byte[] data, CancellationToken ct = default)
    {
        if (_session == null || !Connected) return false;

        try
        {
            var val = data.Length >= 4
                ? new Variant(BitConverter.ToInt32(data))
                : new Variant((int)data[0]);

            var nodeId = new NodeId(address);
            var writeValue = new WriteValue
            {
                NodeId = nodeId,
                AttributeId = Attributes.Value,
                Value = new DataValue(val)
            };

            var response = await _session.WriteAsync(null, [writeValue], ct);
            var ok = response?.Results != null && response.Results.Count > 0 && response.Results[0] == StatusCodes.Good;
            if (ok) CountWrite();
            return ok;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "OPC UA [{Name}] write {Addr} failed", Name, address);
            return false;
        }
    }

    private async Task<ApplicationConfiguration> CreateApplicationConfig()
    {
        var appName = $"WcsCore.{Name}";
        var config = new ApplicationConfiguration
        {
            ApplicationName = appName,
            ApplicationType = ApplicationType.Client,
            ApplicationUri = $"urn:WcsCore:{Name}",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.X509Store,
                    StorePath = "CurrentUser\\UA_MachineDefault"
                },
                TrustedIssuerCertificates = new CertificateTrustList(),
                TrustedPeerCertificates = new CertificateTrustList(),
                AutoAcceptUntrustedCertificates = true,
                RejectSHA1SignedCertificates = false,
                MinimumCertificateKeySize = 1024,
            },
            TransportQuotas = new TransportQuotas { OperationTimeout = _config.TimeoutMs },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 },
        };
        config.TransportConfigurations = [];
        return config;
    }

    private void OnKeepAlive(ISession session, KeepAliveEventArgs e)
    {
        if (ServiceResult.IsNotGood(e.Status))
        {
            Logger.LogWarning("OPC UA [{Name}] keep-alive lost", Name);
            if (_session != null)
            {
                _reconnectHandler?.Dispose();
                _reconnectHandler = new SessionReconnectHandler();
                _reconnectHandler.BeginReconnect(_session, _config.ReconnectIntervalMs, (_, _) =>
                {
                    Logger.LogInformation("OPC UA [{Name}] reconnected", Name);
                });
            }
        }
    }

    public override void Dispose()
    {
        _reconnectHandler?.Dispose();
        _session?.CloseAsync().GetAwaiter().GetResult();
        _session?.Dispose();
        base.Dispose();
    }
}
