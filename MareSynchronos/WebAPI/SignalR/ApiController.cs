﻿using MareSynchronos.API.Routes;
using MareSynchronos.Utils;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using MareSynchronos.API.Dto;
using MareSynchronos.API.SignalR;
using Dalamud.Utility;
using System.Reflection;
using MareSynchronos.WebAPI.SignalR.Utils;
using MareSynchronos.WebAPI.SignalR;
using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Services.Mediator;
using MareSynchronos.Services.ServerConfiguration;
using MareSynchronos.Services;
using MareSynchronos.API.Data.Extensions;
using MareSynchronos.API.Data;
using System.Net.Http.Headers;

namespace MareSynchronos.WebAPI;

public sealed partial class ApiController : DisposableMediatorSubscriberBase, IMareHubClient
{
    public const string MainServer = "Lunae Crescere Incipientis (Central Server EU)";
    public const string MainServiceUri = "wss://maresynchronos.com";

    private readonly DalamudUtilService _dalamudUtil;
    private readonly HubFactory _hubFactory;
    private readonly PairManager _pairManager;
    private readonly ServerConfigurationManager _serverManager;
    private CancellationTokenSource _connectionCancellationTokenSource;
    private ConnectionDto? _connectionDto;
    private bool _doNotNotifyOnNextInfo = false;
    private CancellationTokenSource? _healthCheckTokenSource = new();
    private bool _initialized;
    private HubConnection? _mareHub;
    private ServerState _serverState;

    public ApiController(ILogger<ApiController> logger, HubFactory hubFactory, DalamudUtilService dalamudUtil,
        PairManager pairManager, ServerConfigurationManager serverManager, MareMediator mediator) : base(logger, mediator)
    {
        _hubFactory = hubFactory;
        _dalamudUtil = dalamudUtil;
        _pairManager = pairManager;
        _serverManager = serverManager;
        _connectionCancellationTokenSource = new CancellationTokenSource();

        Mediator.Subscribe<DalamudLoginMessage>(this, (_) => DalamudUtilOnLogIn());
        Mediator.Subscribe<DalamudLogoutMessage>(this, (_) => DalamudUtilOnLogOut());
        Mediator.Subscribe<HubClosedMessage>(this, (msg) => MareHubOnClosed(msg.Exception));
        Mediator.Subscribe<HubReconnectedMessage>(this, (msg) => _ = Task.Run(MareHubOnReconnected));
        Mediator.Subscribe<HubReconnectingMessage>(this, (msg) => MareHubOnReconnecting(msg.Exception));
        Mediator.Subscribe<CyclePauseMessage>(this, (msg) => CyclePause(msg.UserData));

        ServerState = ServerState.Offline;

        if (_dalamudUtil.IsLoggedIn)
        {
            DalamudUtilOnLogIn();
        }
    }

    public string AuthFailureMessage { get; private set; } = string.Empty;

    public Version CurrentClientVersion => _connectionDto?.CurrentClientVersion ?? new Version(0, 0, 0);

    public string DisplayName => _connectionDto?.User.AliasOrUID ?? string.Empty;

    public bool IsConnected => ServerState == ServerState.Connected;

    public bool IsCurrentVersion => (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0)) >= (_connectionDto?.CurrentClientVersion ?? new Version(0, 0, 0, 0));

    public int OnlineUsers => SystemInfoDto.OnlineUsers;

    public bool ServerAlive => ServerState is ServerState.Connected or ServerState.RateLimited or ServerState.Unauthorized or ServerState.Disconnected;

    public ServerInfo ServerInfo => _connectionDto?.ServerInfo ?? new ServerInfo();

    public ServerState ServerState
    {
        get => _serverState;
        private set
        {
            Logger.LogDebug("New ServerState: {value}, prev ServerState: {_serverState}", value, _serverState);
            _serverState = value;
        }
    }

    public SystemInfoDto SystemInfoDto { get; private set; } = new();

    public string UID => _connectionDto?.User.UID ?? string.Empty;

    public async Task<bool> CheckClientHealth()
    {
        return await _mareHub!.InvokeAsync<bool>(nameof(CheckClientHealth)).ConfigureAwait(false);
    }

    public async Task CreateConnections(bool forceGetToken = false)
    {
        Logger.LogDebug("CreateConnections called");

        if (_serverManager.CurrentServer?.FullPause ?? true)
        {
            Logger.LogInformation("Not recreating Connection, paused");
            _connectionDto = null;
            await StopConnection(ServerState.Disconnected).ConfigureAwait(false);
            _connectionCancellationTokenSource.Cancel();
            return;
        }

        var secretKey = _serverManager.GetSecretKey();
        if (secretKey.IsNullOrEmpty())
        {
            Logger.LogWarning("No secret key set for current character");
            _connectionDto = null;
            await StopConnection(ServerState.NoSecretKey).ConfigureAwait(false);
            _connectionCancellationTokenSource.Cancel();
            return;
        }

        await StopConnection(ServerState.Disconnected).ConfigureAwait(false);

        Logger.LogInformation("Recreating Connection");

        _connectionCancellationTokenSource.Cancel();
        _connectionCancellationTokenSource = new CancellationTokenSource();
        var token = _connectionCancellationTokenSource.Token;
        while (ServerState is not ServerState.Connected && !token.IsCancellationRequested)
        {
            AuthFailureMessage = string.Empty;

            await StopConnection(ServerState.Disconnected).ConfigureAwait(false);
            ServerState = ServerState.Connecting;

            try
            {
                Logger.LogDebug("Building connection");

                if (_serverManager.GetToken() == null || forceGetToken)
                {
                    Logger.LogDebug("Requesting new JWT");
                    using HttpClient httpClient = new();
                    var ver = Assembly.GetExecutingAssembly().GetName().Version;
                    httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MareSynchronos", ver!.Major + "." + ver!.Minor + "." + ver!.Build));
                    var postUri = MareAuth.AuthFullPath(new Uri(_serverManager.CurrentApiUrl
                        .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
                        .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)));
                    var auth = secretKey.GetHash256();
                    var result = await httpClient.PostAsync(postUri, new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("auth", auth),
                        new KeyValuePair<string, string>("charaIdent", await _dalamudUtil.GetPlayerNameHashedAsync().ConfigureAwait(false)),
                    })).ConfigureAwait(false);
                    AuthFailureMessage = await result.Content.ReadAsStringAsync().ConfigureAwait(false);
                    result.EnsureSuccessStatusCode();
                    _serverManager.SaveToken(await result.Content.ReadAsStringAsync().ConfigureAwait(false));
                    Logger.LogDebug("JWT Success");
                }

                while (!await _dalamudUtil.GetIsPlayerPresentAsync().ConfigureAwait(false) && !token.IsCancellationRequested)
                {
                    Logger.LogDebug("Player not loaded in yet, waiting");
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                }

                if (token.IsCancellationRequested) break;

                _mareHub = _hubFactory.GetOrCreate();

                await _mareHub.StartAsync(token).ConfigureAwait(false);

                InitializeApiHooks();
                await LoadIninitialPairs().ConfigureAwait(false);

                _connectionDto = await GetConnectionDto().ConfigureAwait(false);

                ServerState = ServerState.Connected;

                var currentClientVer = Assembly.GetExecutingAssembly().GetName().Version!;

                if (_connectionDto.ServerVersion != IMareHub.ApiVersion)
                {
                    await StopConnection(ServerState.VersionMisMatch).ConfigureAwait(false);
                    if (_connectionDto.CurrentClientVersion > currentClientVer)
                    {
                        Mediator.Publish(new NotificationMessage("Client incompatible",
                            $"Your client is outdated ({currentClientVer.Major}.{currentClientVer.Minor}.{currentClientVer.Build}), current is: " +
                            $"{_connectionDto.CurrentClientVersion.Major}.{_connectionDto.CurrentClientVersion.Minor}.{_connectionDto.CurrentClientVersion.Build}. " +
                            $"This client version is incompatible and will not be able to connect. Please update your Mare Synchronos client.",
                            Dalamud.Interface.Internal.Notifications.NotificationType.Error));
                    }
                    return;
                }

                if (_connectionDto.CurrentClientVersion > currentClientVer)
                {
                    Mediator.Publish(new NotificationMessage("Client outdated",
                        $"Your client is oudated ({currentClientVer.Major}.{currentClientVer.Minor}.{currentClientVer.Build}), current is: " +
                        $"{_connectionDto.CurrentClientVersion.Major}.{_connectionDto.CurrentClientVersion.Minor}.{_connectionDto.CurrentClientVersion.Build}. " +
                        $"Please keep your Mare Synchronos client up-to-date.",
                        Dalamud.Interface.Internal.Notifications.NotificationType.Error));
                }

                await LoadOnlinePairs().ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                Logger.LogWarning(ex, "HttpRequestException on Connection");

                if (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    await StopConnection(ServerState.Unauthorized).ConfigureAwait(false);
                    return;
                }

                ServerState = ServerState.Reconnecting;
                Logger.LogInformation("Failed to establish connection, retrying");
                await Task.Delay(TimeSpan.FromSeconds(new Random().Next(5, 20)), token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Exception on Connection");

                Logger.LogInformation("Failed to establish connection, retrying");
                await Task.Delay(TimeSpan.FromSeconds(new Random().Next(5, 20)), token).ConfigureAwait(false);
            }
        }
    }

    public Task CyclePause(UserData userData)
    {
        CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        Task.Run(async () =>
        {
            var pair = _pairManager.GetOnlineUserPairs().Single(p => p.UserPair != null && p.UserData == userData);
            var perm = pair.UserPair!.OwnPermissions;
            perm.SetPaused(true);
            await UserSetPairPermissions(new API.Dto.User.UserPermissionsDto(userData, perm)).ConfigureAwait(false);
            // wait until it's changed
            while (pair.UserPair!.OwnPermissions != perm)
            {
                await Task.Delay(250, cts.Token).ConfigureAwait(false);
                Logger.LogTrace("Waiting for permissions change for {data}", userData);
            }
            perm.SetPaused(false);
            await UserSetPairPermissions(new API.Dto.User.UserPermissionsDto(userData, perm)).ConfigureAwait(false);
        }, cts.Token).ContinueWith((t) => cts.Dispose());

        return Task.CompletedTask;
    }

    public async Task<ConnectionDto> GetConnectionDto()
    {
        var dto = await _mareHub!.InvokeAsync<ConnectionDto>(nameof(GetConnectionDto)).ConfigureAwait(false);
        Mediator.Publish(new ConnectedMessage(dto));
        return dto;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        _healthCheckTokenSource?.Cancel();
        Task.Run(async () => await StopConnection(ServerState.Disconnected).ConfigureAwait(false));
        _connectionCancellationTokenSource?.Cancel();
    }

    private async Task ClientHealthCheck(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _mareHub != null)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            _ = await CheckClientHealth().ConfigureAwait(false);
            Logger.LogDebug("Checked Client Health State");
        }
    }

    private void DalamudUtilOnLogIn()
    {
        Task.Run(() => CreateConnections(forceGetToken: true));
    }

    private void DalamudUtilOnLogOut()
    {
        Task.Run(async () => await StopConnection(ServerState.Disconnected).ConfigureAwait(false));
        ServerState = ServerState.Offline;
    }

    private void InitializeApiHooks()
    {
        if (_mareHub == null) return;

        Logger.LogDebug("Initializing data");
        OnDownloadReady((guid) => Client_DownloadReady(guid));
        OnReceiveServerMessage((sev, msg) => Client_ReceiveServerMessage(sev, msg));
        OnUpdateSystemInfo((dto) => Client_UpdateSystemInfo(dto));

        OnUserSendOffline((dto) => Client_UserSendOffline(dto));
        OnUserAddClientPair((dto) => Client_UserAddClientPair(dto));
        OnUserReceiveCharacterData((dto) => Client_UserReceiveCharacterData(dto));
        OnUserRemoveClientPair(dto => Client_UserRemoveClientPair(dto));
        OnUserSendOnline(dto => Client_UserSendOnline(dto));
        OnUserUpdateOtherPairPermissions(dto => Client_UserUpdateOtherPairPermissions(dto));
        OnUserUpdateSelfPairPermissions(dto => Client_UserUpdateSelfPairPermissions(dto));
        OnUserReceiveUploadStatus(dto => Client_UserReceiveUploadStatus(dto));
        OnUserUpdateProfile(dto => Client_UserUpdateProfile(dto));

        OnGroupChangePermissions((dto) => Client_GroupChangePermissions(dto));
        OnGroupDelete((dto) => Client_GroupDelete(dto));
        OnGroupPairChangePermissions((dto) => Client_GroupPairChangePermissions(dto));
        OnGroupPairChangeUserInfo((dto) => Client_GroupPairChangeUserInfo(dto));
        OnGroupPairJoined((dto) => Client_GroupPairJoined(dto));
        OnGroupPairLeft((dto) => Client_GroupPairLeft(dto));
        OnGroupSendFullInfo((dto) => Client_GroupSendFullInfo(dto));
        OnGroupSendInfo((dto) => Client_GroupSendInfo(dto));

        _healthCheckTokenSource?.Cancel();
        _healthCheckTokenSource?.Dispose();
        _healthCheckTokenSource = new CancellationTokenSource();
        _ = ClientHealthCheck(_healthCheckTokenSource.Token);

        _initialized = true;
    }

    private async Task LoadIninitialPairs()
    {
        foreach (var userPair in await UserGetPairedClients().ConfigureAwait(false))
        {
            Logger.LogDebug("Individual Pair: {userPair}", userPair);
            _pairManager.AddUserPair(userPair, addToLastAddedUser: false);
        }
        foreach (var entry in await GroupsGetAll().ConfigureAwait(false))
        {
            Logger.LogDebug("Group: {entry}", entry);
            _pairManager.AddGroup(entry);
        }
        foreach (var group in _pairManager.GroupPairs.Keys)
        {
            var users = await GroupsGetUsersInGroup(group).ConfigureAwait(false);
            foreach (var user in users)
            {
                Logger.LogDebug("Group Pair: {user}", user);
                _pairManager.AddGroupPair(user);
            }
        }
    }

    private async Task LoadOnlinePairs()
    {
        foreach (var entry in await UserGetOnlinePairs().ConfigureAwait(false))
        {
            Logger.LogDebug("Pair online: {pair}", entry);
            _pairManager.MarkPairOnline(entry, sendNotif: false);
        }
    }

    private void MareHubOnClosed(Exception? arg)
    {
        _healthCheckTokenSource?.Cancel();
        Mediator.Publish(new DisconnectedMessage());
        ServerState = ServerState.Offline;
        if (arg != null)
        {
            Logger.LogWarning(arg, "Connection closed");
        }
        else
        {
            Logger.LogInformation("Connection closed");
        }
    }

    private async Task MareHubOnReconnected()
    {
        ServerState = ServerState.Connecting;
        try
        {
            InitializeApiHooks();
            await LoadIninitialPairs().ConfigureAwait(false);
            _connectionDto = await GetConnectionDto().ConfigureAwait(false);
            if (_connectionDto.ServerVersion != IMareHub.ApiVersion)
            {
                await StopConnection(ServerState.VersionMisMatch).ConfigureAwait(false);
                return;
            }
            await LoadOnlinePairs().ConfigureAwait(false);
            ServerState = ServerState.Connected;
        }
        catch (Exception ex)
        {
            Logger.LogCritical(ex, "Failure to obtain data after reconnection");
            await StopConnection(ServerState.Disconnected).ConfigureAwait(false);
        }
    }

    private void MareHubOnReconnecting(Exception? arg)
    {
        _doNotNotifyOnNextInfo = true;
        _healthCheckTokenSource?.Cancel();
        ServerState = ServerState.Reconnecting;
        Logger.LogWarning(arg, "Connection closed... Reconnecting");
    }

    private async Task StopConnection(ServerState state)
    {
        ServerState = ServerState.Disconnecting;

        if (_mareHub is not null)
        {
            _initialized = false;
            _healthCheckTokenSource?.Cancel();
            Logger.LogInformation("Stopping existing connection");
            await _hubFactory.DisposeHubAsync().ConfigureAwait(false);
            Mediator.Publish(new DisconnectedMessage());
            _mareHub = null;
            _connectionDto = null;
        }

        ServerState = state;
    }
}