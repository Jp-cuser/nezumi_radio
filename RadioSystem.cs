using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using Discord;
using Discord.WebSocket;
using Discord.Interactions;
using Lavalink4NET;
using Lavalink4NET.Players;
using Lavalink4NET.Players.Queued;
using Lavalink4NET.Tracks;
using Lavalink4NET.Events.Players;
using Lavalink4NET.Extensions;
using Lavalink4NET.Rest.Entities.Tracks;
using Lavalink4NET.Filters;
using Lavalink4NET.Protocol.Models.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NezumiRadio
{
    public class RadioSystem : BackgroundService
    {
        public List<BotUnit> Units { get; } = new();
        private readonly IServiceProvider _services;
        private readonly AudiusApiService _audius;
        private readonly ILogger<RadioSystem> _logger;
        private const string StateFile = "radio_system_state.json";
        public const ulong ProductionGuildId = 1450709451488100396;
        public const ulong TestGuildId = 1483795902610145463;

        private readonly ulong[] _targetCategoryIds = { 1450712250514935960, 1483795904183140373 };
        private readonly Dictionary<ulong, ulong> _lastControllerMessageIds = new();

        private bool _isEmergencyStopping = false;
        private const float DefaultVolume = 0.14f;

        public RadioSystem(IServiceProvider services, AudiusApiService audius, ILogger<RadioSystem> logger)
        {
            _services = services; _audius = audius; _logger = logger;
        }

        private void SaveGlobalState()
        {
            try {
                var state = new SystemState();
                foreach (var u in Units) state.Units[u.Index] = new UnitState { IsActive = u.IsActive, TargetVoiceChannelId = u.TargetVoiceChannelId };
                File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
            } catch { }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            SystemState? initialState = null;
            if (File.Exists(StateFile)) { 
                try { 
                    initialState = JsonSerializer.Deserialize<SystemState>(File.ReadAllText(StateFile)); 
                    _logger.LogInformation("Loaded existing system state.");
                } catch { } 
            }

            _logger.LogInformation("System starting: Checking Lavalink status...");
            await WaitForLavalinkReadyAsync(stoppingToken);

            for (int i = 0; i < 6; i++)
            {
                var token = Environment.GetEnvironmentVariable($"BOT_TOKEN_{i}");
                if (string.IsNullOrEmpty(token)) continue;

                await Task.Delay(1000);

                var client = new DiscordSocketClient(new DiscordSocketConfig { 
                    GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.GuildVoiceStates | GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent,
                    AlwaysDownloadUsers = true
                });

                var sc = new ServiceCollection();
                sc.AddHttpClient();
                sc.AddSingleton(client);
                sc.AddLavalink();
                sc.ConfigureLavalink(options => {
                    options.BaseAddress = new Uri(Environment.GetEnvironmentVariable("LAVALINK_URL") ?? "http://localhost:2333");
                    options.Passphrase = Environment.GetEnvironmentVariable("LAVALINK_PASSWORD") ?? "youshallnotpass";
                });

                var sp = sc.BuildServiceProvider();
                var audio = sp.GetRequiredService<IAudioService>();
                var interaction = new InteractionService(client);
                var unit = new BotUnit { Index = i, Client = client, AudioService = audio, InteractionService = interaction };
                
                if (initialState != null && initialState.Units.ContainsKey(i)) {
                    unit.IsActive = initialState.Units[i].IsActive;
                    unit.TargetVoiceChannelId = initialState.Units[i].TargetVoiceChannelId;
                }
                
                Units.Add(unit);

                if (i == 0) {
                    client.MessageReceived += async (msg) => await HandleStickyControllerAsync(client, msg);
                    client.UserVoiceStateUpdated += async (u, oldState, newState) => {
                        if (oldState.VoiceChannel == null && newState.VoiceChannel != null && !u.IsBot) {
                            var categoryChannels = newState.VoiceChannel.Guild.TextChannels.Where(c => _targetCategoryIds.Contains(c.CategoryId ?? 0));
                            foreach (var ch in categoryChannels) {
                                await TriggerStickyRefreshAsync(client, ch);
                            }
                        }
                    };
                }

                client.ButtonExecuted += async (btn) => await HandleControllerButtonsAsync(btn);

                audio.TrackStarted += async (s, e) => {
                    if (e.Player.GuildId == ProductionGuildId || e.Player.GuildId == TestGuildId) {
                        unit.TrackStartTime = DateTime.Now;
                        unit.CurrentTrackTitle = e.Track.Title;
                        unit.LastPosition = TimeSpan.Zero;
                        unit.StuckCounter = 0;
                        await unit.Client.SetGameAsync(unit.CurrentTrackTitle, null, ActivityType.Listening);
                        await HandleTrackStarted(unit, e);
                        await UpdateAllControllersAsync();
                    }
                };
                
                audio.TrackEnded += async (s, e) => {
                    if (e.Player.GuildId == ProductionGuildId || e.Player.GuildId == TestGuildId) {
                        if (e.Reason.ToString().Contains("Finished", StringComparison.OrdinalIgnoreCase) || e.Reason.ToString().Contains("Replaced", StringComparison.OrdinalIgnoreCase)) {
                            await HandleTrackEnded(unit, stoppingToken);
                            await UpdateAllControllersAsync();
                        }
                    }
                };

                audio.TrackException += async (s, e) => { await HandleTrackEnded(unit, stoppingToken); await UpdateAllControllersAsync(); };
                audio.TrackStuck += async (s, e) => { await HandleTrackEnded(unit, stoppingToken); await UpdateAllControllersAsync(); };

                client.Ready += async () => {
                    _ = Task.Run(async () => {
                        try {
                            if (unit.Index == 0) await interaction.RegisterCommandsGloballyAsync();
                            else { var cmds = await client.GetGlobalApplicationCommandsAsync(); foreach (var c in cmds) await c.DeleteAsync(); }
                        } catch { }
                    });
                    await UpdateBotProfile(unit, GetKatakanaGenre(GetAtmosphericGenre(unit.Index / 2, DateTime.Now.Hour)));
                    _ = RunUnitLoopAsync(unit, stoppingToken);
                };

                client.InteractionCreated += async (x) => {
                    var ctx = new SocketInteractionContext(client, x);
                    await interaction.ExecuteCommandAsync(ctx, _services);
                };
                await interaction.AddModuleAsync<RadioModule>(_services);

                await audio.StartAsync(stoppingToken);
                await client.LoginAsync(TokenType.Bot, token);
                await client.StartAsync();
            }

            while (!stoppingToken.IsCancellationRequested) { SaveGlobalState(); await Task.Delay(10000, stoppingToken); }
        }

        #region Sticky Controller Logic

        private async Task HandleStickyControllerAsync(DiscordSocketClient client, SocketMessage msg)
        {
            if (msg.Author.IsBot) return;
            var channel = msg.Channel as SocketTextChannel;
            if (channel == null || !_targetCategoryIds.Contains(channel.CategoryId ?? 0)) return;

            await TriggerStickyRefreshAsync(client, channel);
        }

        private async Task TriggerStickyRefreshAsync(DiscordSocketClient client, SocketTextChannel channel)
        {
            if (_lastControllerMessageIds.TryGetValue(channel.Id, out ulong lastId)) {
                try { var oldMsg = await channel.GetMessageAsync(lastId); if (oldMsg != null) await oldMsg.DeleteAsync(); } catch { }
            }

            var (embed, components) = BuildController(channel.Guild.Id);
            var newMsg = await channel.SendMessageAsync(embed: embed, components: components);
            _lastControllerMessageIds[channel.Id] = newMsg.Id;
        }

        private (Embed, MessageComponent) BuildController(ulong guildId)
        {
            var embed = new EmbedBuilder()
                .WithTitle("📻 NEZUMI RADIO 放送管理パネル")
                .WithDescription("ボタンを押すとボットがVCに来ます。")
                .WithColor(Color.Blue);

            for (int i = 0; i < 3; i++) {
                var u1 = Units.FirstOrDefault(u => u.Index == i * 2);
                var u2 = Units.FirstOrDefault(u => u.Index == (i * 2) + 1);

                string s1 = GetUnitStatusEmoji(u1, guildId);
                string s2 = GetUnitStatusEmoji(u2, guildId);
                
                embed.AddField($"{GetGroupLabel(i)} ステーション", 
                    $"{s1} A機 {s2} B機");
            }

            embed.AddField("状態の説明", 
                "🟢：このサーバーで放送中\n" +
                "🟡：他のサーバーで放送中（呼べません）\n" +
                "⚪：待機中（呼ぶことができます）");

            var builder = new ComponentBuilder();
            builder.WithButton("Dance & High Energy", "ctrl_0", ButtonStyle.Success, row: 0);
            builder.WithButton("Urban & Groove", "ctrl_1", ButtonStyle.Success, row: 0);
            builder.WithButton("Chill & Relax", "ctrl_2", ButtonStyle.Success, row: 0);
            builder.WithButton("退出", "ctrl_leave", ButtonStyle.Danger, row: 1);

            return (embed.Build(), builder.Build());
        }

        private string GetUnitStatusEmoji(BotUnit? unit, ulong guildId) {
            if (unit == null || !unit.IsActive || unit.TargetVoiceChannelId == 0) return "⚪";
            
            // プレイヤー情報を取得
            var player = GetPlayerAsync(unit).GetAwaiter().GetResult();
            
            // テストサーバーにいる場合は、どこから見ても⚪（呼べる状態）にする
            if (player != null && player.GuildId == TestGuildId) return "⚪";

            // それ以外の場所で、現在のパネルを表示しているサーバーにいる場合のみ🟢
            if (unit.Client.GetGuild(guildId)?.GetVoiceChannel(unit.TargetVoiceChannelId) != null) return "🟢";
            
            // 他の本番サーバーにいる場合は🟡
            return "🟡";
        }

        private string GetGroupLabel(int group) => group switch { 
            0 => "Dance & High Energy", 
            1 => "Urban & Groove", 
            2 => "Chill & Relax", 
            _ => "Other" 
        };

        private async Task UpdateAllControllersAsync()
        {
            foreach (var kvp in _lastControllerMessageIds) {
                try {
                    var unit = Units.FirstOrDefault();
                    if (unit == null) continue;
                    var channel = unit.Client.GetChannel(kvp.Key) as SocketTextChannel;
                    if (channel != null) {
                        var (embed, components) = BuildController(channel.Guild.Id);
                        var msg = await channel.GetMessageAsync(kvp.Value) as IUserMessage;
                        if (msg != null) await msg.ModifyAsync(x => { x.Embed = embed; x.Components = components; });
                    }
                } catch { }
            }
        }

        private async Task HandleControllerButtonsAsync(SocketMessageComponent btn)
        {
            var user = btn.User as IGuildUser;
            var vcId = user?.VoiceChannel?.Id ?? 0;
            if (vcId == 0) { await btn.RespondAsync("先にボイスチャンネルに入ってください！", ephemeral: true); return; }

            if (btn.Data.CustomId == "ctrl_leave") {
                var botsInVC = Units.Where(u => u.IsActive && u.TargetVoiceChannelId == vcId).ToList();
                if (botsInVC.Count == 0) { await btn.RespondAsync("このVCにボットはいません。", ephemeral: true); return; }

                foreach (var b in botsInVC) {
                    b.IsActive = false;
                    b.TargetVoiceChannelId = 0;
                }
                await btn.RespondAsync("すべてのボットを退出させました。", ephemeral: true);
            }
            else if (btn.Data.CustomId.StartsWith("ctrl_")) {
                int stationIndex = int.Parse(btn.Data.CustomId.Split('_')[1]);
                var groupUnits = Units.Where(u => u.Index / 2 == stationIndex).ToList();

                var activeInThisVC = groupUnits.FirstOrDefault(u => u.IsActive && u.TargetVoiceChannelId == vcId);
                if (activeInThisVC != null) {
                    activeInThisVC.IsActive = false;
                    activeInThisVC.TargetVoiceChannelId = 0;
                    await btn.RespondAsync($"{GetGroupLabel(stationIndex)} ステーションを退出させました。", ephemeral: true);
                } else {
                    var otherBotInThisVC = Units.FirstOrDefault(u => u.IsActive && u.TargetVoiceChannelId == vcId);
                    if (otherBotInThisVC != null) {
                        otherBotInThisVC.IsActive = false;
                        otherBotInThisVC.TargetVoiceChannelId = 0;
                    }

                    var availableUnit = groupUnits.FirstOrDefault(u => {
                        if (!u.IsActive || u.TargetVoiceChannelId == 0) return true;
                        var p = GetPlayerAsync(u).GetAwaiter().GetResult();
                        return p != null && p.GuildId == TestGuildId;
                    });

                    if (availableUnit != null) {
                        var player = await GetPlayerAsync(availableUnit);
                        if (player != null && player.VoiceChannelId != 0 && player.GuildId != TestGuildId && player.GuildId != btn.GuildId) {
                            availableUnit = groupUnits.LastOrDefault(u => {
                                if (!u.IsActive || u.TargetVoiceChannelId == 0) return true;
                                var p = GetPlayerAsync(u).GetAwaiter().GetResult();
                                return p != null && p.GuildId == TestGuildId;
                            });
                            player = await GetPlayerAsync(availableUnit!);
                            if (player != null && player.VoiceChannelId != 0 && player.GuildId != TestGuildId && player.GuildId != btn.GuildId) {
                                await btn.RespondAsync($"⚠️ 他の本番サーバーで稼働中のため呼べません。", ephemeral: true);
                                return;
                            }
                        }

                        availableUnit!.IsActive = true;
                        availableUnit.TargetVoiceChannelId = vcId;
                        await btn.RespondAsync($"{GetGroupLabel(stationIndex)} ステーションを呼びました。", ephemeral: true);
                    }
                }
            }
            await UpdateAllControllersAsync();
        }

        #endregion

        private async Task<bool> IsLavalinkReadyAsync(CancellationToken ct)
        {
            var url = (Environment.GetEnvironmentVariable("LAVALINK_URL") ?? "http://localhost:2333").TrimEnd('/') + "/version";
            var password = Environment.GetEnvironmentVariable("LAVALINK_PASSWORD") ?? "youshallnotpass";
            
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("Authorization", password);
            try {
                var res = await http.GetAsync(url, ct);
                return res.IsSuccessStatusCode;
            } catch { return false; }
        }

        private async Task WaitForLavalinkReadyAsync(CancellationToken ct)
        {
            for (int i = 0; i < 60; i++) {
                if (await IsLavalinkReadyAsync(ct)) { _logger.LogInformation("Lavalink is ready!"); return; }
                _logger.LogInformation($"Waiting for Lavalink... ({i+1}/60)");
                await Task.Delay(1000, ct);
            }
            _logger.LogWarning("Lavalink wait timed out. Proceeding anyway.");
        }

        private async Task ApplyNormalizerFilters(LavalinkPlayer player)
        {
            try {
                var method = player.GetType().GetMethod("SetVolumeAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);
                if (method != null) {
                    var result = method.Invoke(player, new object[] { DefaultVolume, null! });
                    if (result is ValueTask vt) await vt;
                }
            } catch { }
        }

        private async Task HandleTrackEnded(BotUnit unit, CancellationToken ct)
        {
            if (!unit.IsActive || _isEmergencyStopping) return;
            await unit.Lock.WaitAsync(ct);
            try {
                if (unit.VirtualQueue.Count > 0) unit.VirtualQueue.RemoveAt(0);
                await FillVirtualQueueAsync(unit, ct);
                if (unit.VirtualQueue.Count > 0) {
                    var player = await GetPlayerAsync(unit);
                    if (player != null && player.VoiceChannelId == unit.TargetVoiceChannelId) {
                        await ApplyNormalizerFilters(player);
                        await player.PlayAsync(unit.VirtualQueue[0]);
                    }
                }
            } finally { unit.Lock.Release(); }
        }

        private async Task RunUnitLoopAsync(BotUnit unit, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested) {
                try {
                    while (_isEmergencyStopping && !ct.IsCancellationRequested) { await Task.Delay(500, ct); }
                    await TickUnitAsync(unit, ct); 
                } catch (Exception ex) { 
                    _logger.LogError($"[Unit {unit.Index+1:D2}] Tick Error: {ex.Message}"); 
                }
                await Task.Delay(5000, ct);
            }
        }

        private async Task<QueuedLavalinkPlayer?> GetPlayerAsync(BotUnit unit)
        {
            return await unit.AudioService.Players.GetPlayerAsync<QueuedLavalinkPlayer>(ProductionGuildId) 
                   ?? await unit.AudioService.Players.GetPlayerAsync<QueuedLavalinkPlayer>(TestGuildId);
        }

        private async Task HandleTrackStarted(BotUnit unit, TrackStartedEventArgs e)
        {
            if (!unit.IsActive || unit.TargetVoiceChannelId == 0) return;
            var channel = await unit.Client.GetChannelAsync(unit.TargetVoiceChannelId) as IMessageChannel;
            if (channel == null) return;
            
            string? jinglePath = Environment.GetEnvironmentVariable("JINGLE_URL");
            bool isJingle = e.Track.Title.Contains("jingle", StringComparison.OrdinalIgnoreCase) || 
                            e.Track.Title.Contains(".mp3", StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrEmpty(jinglePath) && e.Track.Uri?.ToString().Contains(jinglePath, StringComparison.OrdinalIgnoreCase) == true);

            if (isJingle) return;

            var embed = new EmbedBuilder().WithTitle("📻 Now Playing").WithDescription($"**[{e.Track.Title}]({e.Track.Uri})**").WithAuthor(e.Track.Author).AddField("Genre", unit.CurrentGenreKatakana, true).AddField("Unit", unit.Client.CurrentUser.Username, true).WithColor(Color.Blue).Build();
            await channel.SendMessageAsync(embed: embed);
        }

        public async Task TriggerUnitTickAsync(int index) { var unit = Units.FirstOrDefault(u => u.Index == index); if (unit != null) await TickUnitAsync(unit, CancellationToken.None); }

        private async Task TickUnitAsync(BotUnit unit, CancellationToken ct)
        {
            if (!await unit.Lock.WaitAsync(0)) return;
            try {
                int groupIndex = unit.Index / 2;
                string targetGenre = GetAtmosphericGenre(groupIndex, DateTime.Now.Hour);
                unit.CurrentGenreKatakana = GetKatakanaGenre(targetGenre);
                
                bool isGenreMismatched = string.IsNullOrEmpty(unit.LastGenre) || unit.LastGenre != targetGenre;
                
                if (isGenreMismatched && !string.IsNullOrEmpty(unit.LastGenre) && unit.Index == 0 && !_isEmergencyStopping) {
                    await StopAllPlaybackAsync();
                }

                while (_isEmergencyStopping && !ct.IsCancellationRequested) { await Task.Delay(500, ct); }

                bool jingleReady = await PrepareTracksOnDemandAsync(unit, targetGenre, ct);
                bool justJoined = await HandlePhysicalConnectionAsync(unit, ct);
                
                if (justJoined) {
                    _logger.LogInformation($"[Unit {unit.Index+1:D2}] Just joined VC. Stabilizing for 2s...");
                    await Task.Delay(2000, ct);
                }

                if (unit.VirtualQueue.Count > 0) {
                    var player = await GetPlayerAsync(unit);
                    var current = unit.VirtualQueue[0];
                    
                    string stateStr = player?.State.ToString() ?? "None";
                    bool isActuallyPlaying = stateStr.Contains("Playing", StringComparison.OrdinalIgnoreCase);
                    bool isBuffering = stateStr.Contains("Buffering", StringComparison.OrdinalIgnoreCase);

                    if (unit.IsActive && player != null && player.VoiceChannelId == unit.TargetVoiceChannelId) {
                        bool isCurrentJingle = current.Title.Contains("jingle", StringComparison.OrdinalIgnoreCase) || current.Title.Contains("Unknown", StringComparison.OrdinalIgnoreCase);
                        
                        if (isGenreMismatched && !isCurrentJingle) {
                            _logger.LogWarning($"[Unit {unit.Index+1:D2}] Queue sync error: First track is not jingle. Retrying...");
                            unit.VirtualQueue.Clear();
                            return;
                        }

                        if (isGenreMismatched && !jingleReady) {
                            _logger.LogWarning($"[Unit {unit.Index+1:D2}] Waiting for jingle to be ready...");
                            return; 
                        }

                        if (isActuallyPlaying && !isGenreMismatched) {
                            TimeSpan currentPos = TimeSpan.Zero;
                            try {
                                var posProp = player.GetType().GetProperty("Position");
                                if (posProp != null) {
                                    var trackPos = posProp.GetValue(player);
                                    if (trackPos != null) {
                                        var valueProp = trackPos.GetType().GetProperty("Value");
                                        if (valueProp != null) {
                                            var val = valueProp.GetValue(trackPos);
                                            if (val is TimeSpan ts) currentPos = ts;
                                        }
                                    }
                                }
                            } catch { }

                            if (currentPos == unit.LastPosition && currentPos != TimeSpan.Zero) {
                                unit.StuckCounter++;
                                if (unit.StuckCounter >= 3) {
                                    _logger.LogWarning($"[Unit {unit.Index+1:D2}] Track stuck detected, skipping...");
                                    unit.VirtualQueue.RemoveAt(0);
                                    await FillVirtualQueueAsync(unit, ct);
                                    await ApplyNormalizerFilters(player);
                                    await player.PlayAsync(unit.VirtualQueue[0]);
                                    unit.StuckCounter = 0;
                                    return;
                                }
                            } else {
                                unit.LastPosition = currentPos;
                                unit.StuckCounter = 0;
                            }
                        }

                        if (DateTime.Now.Minute == 59) {
                            float progress = DateTime.Now.Second / 60.0f;
                            float fadeVolume = DefaultVolume * (1.0f - progress);
                            try {
                                var method = player.GetType().GetMethod("SetVolumeAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);
                                if (method != null) {
                                    var result = method.Invoke(player, new object[] { fadeVolume, null! });
                                    if (result is ValueTask vt) await vt;
                                }
                            } catch { }
                        }

                        if (isGenreMismatched && jingleReady) {
                            _logger.LogInformation($"[Unit {unit.Index+1:D2}] Switching to {targetGenre} - Playing Jingle: {current.Title}");
                            unit.TrackStartTime = DateTime.Now;
                            await player.StopAsync();
                            await ApplyNormalizerFilters(player);
                            await player.PlayAsync(current);
                            unit.LastGenre = targetGenre;
                            await unit.Client.SetGameAsync(current.Title, null, ActivityType.Listening);
                            unit.IsResuming = false;
                        }
                        else if (!isActuallyPlaying && !isBuffering) {
                            var duration = current.Duration == TimeSpan.Zero ? TimeSpan.FromMinutes(3) : current.Duration;
                            if (DateTime.Now > unit.TrackStartTime + duration) {
                                _logger.LogInformation($"[Unit {unit.Index+1:D2}] Track finished (Time-based), moving to next.");
                                unit.VirtualQueue.RemoveAt(0);
                                await FillVirtualQueueAsync(unit, ct);
                                unit.TrackStartTime = DateTime.Now;
                                await ApplyNormalizerFilters(player);
                                await player.PlayAsync(unit.VirtualQueue[0]);
                                unit.IsResuming = false;
                            }
                            else if (!unit.IsResuming) {
                                _logger.LogInformation($"[Unit {unit.Index+1:D2}] Resuming playback at {DateTime.Now - unit.TrackStartTime}");
                                unit.IsResuming = true;
                                await ApplyNormalizerFilters(player);
                                await player.PlayAsync(unit.VirtualQueue[0]);
                                await player.SeekAsync(DateTime.Now - unit.TrackStartTime);
                            }
                        }
                        else if (isActuallyPlaying) {
                            unit.IsResuming = false;
                        }
                    }
                }

                if (DateTime.Now.Minute >= 45 && !unit.IsPreloading) _ = RunPreloadOnDemandAsync(unit, groupIndex);
            } finally { unit.Lock.Release(); }
        }

        private async Task<bool> PrepareTracksOnDemandAsync(BotUnit unit, string targetGenre, CancellationToken ct)
        {
            bool isNewGenre = string.IsNullOrEmpty(unit.LastGenre) || unit.LastGenre != targetGenre;
            if (isNewGenre || unit.VirtualQueue.Count == 0) {
                if (isNewGenre && (unit.VirtualQueue.Count == 0 || !unit.VirtualQueue[0].Title.Contains("jingle", StringComparison.OrdinalIgnoreCase))) {
                    unit.VirtualQueue.Clear();
                    _logger.LogInformation($"[Unit {unit.Index+1:D2}] Preparing for new genre: {targetGenre}");
                    
                    string? jingleUrl = Environment.GetEnvironmentVariable("JINGLE_URL");
                    if (!string.IsNullOrEmpty(jingleUrl)) {
                        try {
                            _logger.LogInformation($"[Unit {unit.Index+1:D2}] Loading jingle: {jingleUrl}");
                            var res = await unit.AudioService.Tracks.LoadTracksAsync(jingleUrl, TrackSearchMode.None, cancellationToken: ct);
                            if (res.Tracks.Length > 0) {
                                _logger.LogInformation($"[Unit {unit.Index+1:D2}] Jingle added to head of queue.");
                                unit.VirtualQueue.Insert(0, res.Tracks[0]);
                            }
                            else {
                                _logger.LogWarning($"[Unit {unit.Index+1:D2}] Jingle not found at URL/Path: {jingleUrl}");
                            }
                        } catch (Exception ex) { 
                            _logger.LogError($"[Unit {unit.Index+1:D2}] Jingle load FAILED: {ex.Message}");
                        }
                    }

                    if (unit.NextUrlPool.Count > 0) {
                        unit.UrlPool = new List<string>(unit.NextUrlPool);
                        unit.NextUrlPool.Clear();
                    } else {
                        var urls = await _audius.GetTracksByGenreAsync(targetGenre);
                        unit.UrlPool = urls.OrderBy(_ => Guid.NewGuid()).ToList();
                    }
                    
                    await UpdateBotProfile(unit, unit.CurrentGenreKatakana);
                }
            }
            await FillVirtualQueueAsync(unit, ct);
            return true;
        }

        private async Task FillVirtualQueueAsync(BotUnit unit, CancellationToken ct)
        {
            while (unit.VirtualQueue.Count < 10 && (unit.UrlPool.Count > 0 || unit.LastGenre != string.Empty)) {
                if (unit.UrlPool.Count == 0) {
                    var targetG = string.IsNullOrEmpty(unit.LastGenre) ? GetAtmosphericGenre(unit.Index/2, DateTime.Now.Hour) : unit.LastGenre;
                    var urls = await _audius.GetTracksByGenreAsync(targetG);
                    unit.UrlPool = urls.OrderBy(_ => Guid.NewGuid()).ToList();
                    if (unit.UrlPool.Count == 0) break;
                }
                string url = unit.UrlPool[0];
                unit.UrlPool.RemoveAt(0);
                try {
                    var res = await unit.AudioService.Tracks.LoadTracksAsync(url, TrackSearchMode.None, cancellationToken: ct);
                    if (res.Tracks.Length > 0) {
                        var track = res.Tracks[0];
                        bool isJingle = track.Title.Contains("jingle", StringComparison.OrdinalIgnoreCase);
                        if (isJingle) { unit.VirtualQueue.Add(track); continue; }
                        bool isInvalidTitle = string.IsNullOrWhiteSpace(track.Title) || track.Title.Contains("Unknown", StringComparison.OrdinalIgnoreCase) || track.Title.Contains("null", StringComparison.OrdinalIgnoreCase);
                        bool isInvalidDuration = track.Duration.TotalSeconds <= 0 || track.Duration.TotalSeconds > 300;
                        if (!isInvalidTitle && !isInvalidDuration) { unit.VirtualQueue.Add(track); }
                    }
                } catch { }
            }
        }

        private async Task<bool> HandlePhysicalConnectionAsync(BotUnit unit, CancellationToken ct)
        {
            if (!unit.IsActive || unit.TargetVoiceChannelId == 0) {
                var p = await GetPlayerAsync(unit);
                if (p != null) await p.DisconnectAsync(ct);
                return false;
            }
            var guild = unit.Client.GetGuild(ProductionGuildId) ?? unit.Client.GetGuild(TestGuildId) ?? unit.Client.Guilds.FirstOrDefault();
            if (guild == null) return false;
            var vc = guild.GetVoiceChannel(unit.TargetVoiceChannelId);
            if (vc == null) return false;
            
            var player = await GetPlayerAsync(unit);
            if (player == null || player.State == PlayerState.Destroyed || player.VoiceChannelId != unit.TargetVoiceChannelId) {
                _logger.LogInformation($"[Unit {unit.Index+1:D2}] Joining Voice Channel: {unit.TargetVoiceChannelId}");
                player = await unit.AudioService.Players.JoinAsync<QueuedLavalinkPlayer, QueuedLavalinkPlayerOptions>(guild.Id, unit.TargetVoiceChannelId, PlayerFactory.Queued, Options.Create(new QueuedLavalinkPlayerOptions { HistoryCapacity = 100 }), cancellationToken: ct);
                if (player != null) { 
                    await ApplyNormalizerFilters(player); 
                    return true;
                }
            }
            return false;
        }

        public async Task StopAllPlaybackAsync()
        {
            if (_isEmergencyStopping) return;
            _isEmergencyStopping = true;
            _logger.LogInformation("Genre change detected: Stopping all units and clearing queues.");
            try {
                foreach (var u in Units) {
                    try {
                        var p = await GetPlayerAsync(u);
                        if (p != null) {
                            await p.StopAsync();
                            u.VirtualQueue.Clear();
                        }
                    } catch { }
                }
                await Task.Delay(1000);
            } finally {
                _isEmergencyStopping = false;
            }
        }

        private async Task RunPreloadOnDemandAsync(BotUnit unit, int groupIndex)
        {
            unit.IsPreloading = true;
            try {
                string nextG = GetAtmosphericGenre(groupIndex, (DateTime.Now.Hour + 1) % 24);
                var urls = await _audius.GetTracksByGenreAsync(nextG);
                unit.NextUrlPool = urls.OrderBy(_ => Guid.NewGuid()).ToList();
            } finally { unit.IsPreloading = false; }
        }

        private async Task UpdateBotProfile(BotUnit unit, string katakanaGenre)
        {
            string newNickname = $"ねずみラジオ {katakanaGenre} {unit.Index + 1:D2}";
            foreach (var guild in unit.Client.Guilds) {
                try {
                    var user = guild.CurrentUser;
                    if (user != null && user.Nickname != newNickname) await user.ModifyAsync(x => x.Nickname = newNickname);
                } catch { }
            }
        }

        private string GetAtmosphericGenre(int groupIndex, int hour) => groupIndex switch { 0 => hour switch { 0 => "Deep House", 1 => "Tech House", 2 => "Techno", 3 => "Jungle", 4 => "Drum & Bass", 5 => "Progressive House", 6 => "Electro", 7 => "Future House", 8 => "House", 9 => "Tropical House", 10 => "Disco", 11 => "Future Bass", 12 => "House", 13 => "Progressive House", 14 => "Tropical House", 15 => "Future Bass", 16 => "Future House", 17 => "Trance", 18 => "Hardstyle", 19 => "Electro", 20 => "Dubstep", 21 => "Trap", 22 => "Techno", 23 => "Deep House", _ => "Techno" }, 1 => hour switch { 0 => "Trap", 1 => "Jersey Club", 2 => "Moombahton", 3 => "Dancehall", 4 => "Glitch Hop", 5 => "Pop", 6 => "Funk", 7 => "R&B/Soul", 8 => "Pop", 9 => "Rock", 10 => "Alternative", 11 => "Punk", 12 => "Rock", 13 => "Alternative", 14 => "Punk", 15 => "Rock", 16 => "Alternative", 17 => "Metal", 18 => "Hyperpop", 19 => "Metal", 20 => "Hip-Hop/Rap", 21 => "Hip-Hop/Rap", 22 => "R&B/Soul", 23 => "Trap", _ => "Rock" }, _ => hour switch { 0 => "Ambient", 1 => "Vaporwave", 2 => "Classical", 3 => "Devotional", 4 => "Ambient", 5 => "Acoustic", 6 => "Jazz", 7 => "Lo-Fi", 8 => "Acoustic", 9 => "World", 10 => "Reggae", 11 => "Latin", 12 => "Folk", 13 => "Country", 14 => "World", 15 => "Reggae", 16 => "Latin", 17 => "Blues", 18 => "Downtempo", 19 => "Jazz", 20 => "Lo-Fi", 21 => "Vaporwave", 22 => "Ambient", 23 => "Lo-Fi", _ => "Ambient" } };
        private string GetKatakanaGenre(string genre) => genre switch { "Electro" => "エレクトロ", "Progressive House" => "プログレハウス", "House" => "ハウス", "Techno" => "テクノ", "Dubstep" => "ダブステップ", "Drum & Bass" => "ドラムンベース", "Future Bass" => "フューチャーベース", "Trap" => "トラップ", "Hip-Hop/Rap" => "ヒップホップ/ラップ", "R&B/Soul" => "R&B/ソウル", "Pop" => "ポップ", "Rock" => "ロック", "Alternative" => "オルタナティブ", "Punk" => "パンク", "Lo-Fi" => "ローファイ", "Ambient" => "アンビエント", "Downtempo" => "ダウンテンポ", "Classical" => "クラシック", "Latin" => "ラテン", "Reggae" => "レゲエ", "Folk" => "フォーク", "World" => "ワールド", "Tech House" => "テックハウス", "Deep House" => "ディープハウス", "Jungle" => "ジャングル", "Tropical House" => "トロピカルハウス", "Disco" => "ディスコ", "Trance" => "トランス", "Hyperpop" => "ハイパーポップ", "Dancehall" => "ダンスホール", "Glitch Hop" => "グリッチホップ", "Jersey Club" => "ジャージークラブ", "Vaporwave" => "ヴェイパーウェイヴ", "Moombahton" => "ムーンバートン", "Acoustic" => "アコースティック", "Jazz" => "ジャズ", "Blues" => "ブルース", "Country" => "カントリー", "Devotional" => "デヴォーショナル", "Hardstyle" => "ハードスタイル", "Future House" => "フューチャーハウス", "Metal" => "メタル", "Funk" => "ファンク", "Mainstage" => "メインステージ", "Kids" => "キッズ", "Podcasts" => "ポッドキャスト", "Audiobooks" => "オーディオブック", "Comedy" => "コメディ", _ => genre };
    }
}
