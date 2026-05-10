using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
            if (File.Exists(StateFile)) { try { initialState = JsonSerializer.Deserialize<SystemState>(File.ReadAllText(StateFile)); } catch { } }

            for (int i = 0; i < 6; i++)
            {
                var token = Environment.GetEnvironmentVariable($"BOT_TOKEN_{i}");
                if (string.IsNullOrEmpty(token)) continue;

                await Task.Delay(1000);

                var client = new DiscordSocketClient(new DiscordSocketConfig { 
                    GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.GuildVoiceStates | GatewayIntents.Guilds,
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

                audio.TrackStarted += async (s, e) => {
                    if (e.Player.GuildId == ProductionGuildId || e.Player.GuildId == TestGuildId) {
                        unit.TrackStartTime = DateTime.Now;
                        unit.CurrentTrackTitle = e.Track.Title;
                        await unit.Client.SetGameAsync(unit.CurrentTrackTitle, null, ActivityType.Listening);
                        await HandleTrackStarted(unit, e);
                    }
                };
                
                audio.TrackEnded += async (s, e) => {
                    if (e.Player.GuildId == ProductionGuildId || e.Player.GuildId == TestGuildId) {
                        if (e.Reason.ToString().Contains("Finished", StringComparison.OrdinalIgnoreCase) || e.Reason.ToString().Contains("Replaced", StringComparison.OrdinalIgnoreCase)) {
                            await HandleTrackEnded(unit, stoppingToken);
                        }
                    }
                };

                audio.TrackException += async (s, e) => { await HandleTrackEnded(unit, stoppingToken); };
                audio.TrackStuck += async (s, e) => { await HandleTrackEnded(unit, stoppingToken); };

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

        private async Task ApplyNormalizerFilters(LavalinkPlayer player)
        {
            await ((dynamic)player).SetVolumeAsync(0.14f);
        }

        private async Task HandleTrackEnded(BotUnit unit, CancellationToken ct)
        {
            if (!unit.IsActive) return;
            await unit.Lock.WaitAsync(ct);
            try {
                if (unit.VirtualQueue.Count > 0) unit.VirtualQueue.RemoveAt(0);
                await FillVirtualQueueAsync(unit, ct);
                if (unit.VirtualQueue.Count > 0) {
                    var player = await GetPlayerAsync(unit);
                    if (player != null && player.VoiceChannelId == unit.TargetVoiceChannelId) {
                        await player.PlayAsync(unit.VirtualQueue[0]);
                    }
                }
            } finally { unit.Lock.Release(); }
        }

        private async Task RunUnitLoopAsync(BotUnit unit, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested) {
                try { await TickUnitAsync(unit, ct); } catch (Exception ex) { Console.WriteLine($"[Unit {unit.Index+1:D2}] Tick Error: {ex.Message}"); }
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
                
                await PrepareTracksOnDemandAsync(unit, targetGenre, ct);
                await HandlePhysicalConnectionAsync(unit, ct);

                if (unit.VirtualQueue.Count > 0) {
                    var player = await GetPlayerAsync(unit);
                    var current = unit.VirtualQueue[0];
                    bool isPlaying = player != null && player.State.ToString().Contains("Playing", StringComparison.OrdinalIgnoreCase);

                    if (unit.IsActive && player != null && player.VoiceChannelId == unit.TargetVoiceChannelId) {
                        if (isGenreMismatched) {
                            unit.TrackStartTime = DateTime.Now;
                            // PlayAsync はデフォルトで前の曲を中断（replace）するが、確実に実行
                            await player.PlayAsync(current);
                            unit.LastGenre = targetGenre;
                        }
                        else if (!isPlaying) {
                            var duration = current.Duration == TimeSpan.Zero ? TimeSpan.FromMinutes(3) : current.Duration;
                            if (DateTime.Now > unit.TrackStartTime + duration) {
                                unit.VirtualQueue.RemoveAt(0);
                                await FillVirtualQueueAsync(unit, ct);
                                unit.TrackStartTime = DateTime.Now;
                                await player.PlayAsync(unit.VirtualQueue[0]);
                            }
                            else {
                                await player.PlayAsync(unit.VirtualQueue[0]);
                                await player.SeekAsync(DateTime.Now - unit.TrackStartTime);
                            }
                        }
                    }
                }

                if (DateTime.Now.Minute >= 45 && !unit.IsPreloading) _ = RunPreloadOnDemandAsync(unit, groupIndex);
            } finally { unit.Lock.Release(); }
        }

        private async Task PrepareTracksOnDemandAsync(BotUnit unit, string targetGenre, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(unit.LastGenre) || unit.LastGenre != targetGenre || unit.VirtualQueue.Count == 0) {
                if (unit.LastGenre != targetGenre || string.IsNullOrEmpty(unit.LastGenre)) {
                    if (unit.NextUrlPool.Count > 0) {
                        unit.UrlPool = new List<string>(unit.NextUrlPool);
                        unit.NextUrlPool.Clear();
                    } else {
                        var urls = await _audius.GetTracksByGenreAsync(targetGenre);
                        unit.UrlPool = urls.OrderBy(_ => Guid.NewGuid()).ToList();
                    }
                    unit.VirtualQueue.Clear();
                    
                    string? jingleUrl = Environment.GetEnvironmentVariable("JINGLE_URL");
                    if (!string.IsNullOrEmpty(jingleUrl)) {
                        try {
                            var res = await unit.AudioService.Tracks.LoadTracksAsync(jingleUrl, TrackSearchMode.None, cancellationToken: ct);
                            if (res.Tracks.Length > 0) unit.VirtualQueue.Add(res.Tracks[0]);
                        } catch (Exception ex) {
                            Console.WriteLine($"[Unit {unit.Index+1:D2}] Jingle Load Exception: {ex.Message}");
                        }
                    }
                    await UpdateBotProfile(unit, unit.CurrentGenreKatakana);
                }
            }
            await FillVirtualQueueAsync(unit, ct);
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
                        // 5分（300秒）以上の曲はスキップ（ジングルは例外的に許可）
                        if (track.Duration.TotalSeconds > 0 && track.Duration.TotalSeconds <= 300) {
                            unit.VirtualQueue.Add(track);
                        } else {
                            Console.WriteLine($"[Unit {unit.Index+1:D2}] Skipping long track: {track.Title} ({track.Duration.TotalSeconds}s)");
                        }
                    }
                } catch { }
            }
        }

        private async Task HandlePhysicalConnectionAsync(BotUnit unit, CancellationToken ct)
        {
            if (!unit.IsActive || unit.TargetVoiceChannelId == 0) {
                var p = await GetPlayerAsync(unit);
                if (p != null) await p.DisconnectAsync(ct);
                return;
            }
            var guild = unit.Client.GetGuild(ProductionGuildId) ?? unit.Client.GetGuild(TestGuildId) ?? unit.Client.Guilds.FirstOrDefault();
            if (guild == null) return;
            var vc = guild.GetVoiceChannel(unit.TargetVoiceChannelId);
            if (vc == null) return;
            var player = await GetPlayerAsync(unit);
            if (player == null || player.State == PlayerState.Destroyed || player.VoiceChannelId != unit.TargetVoiceChannelId) {
                player = await unit.AudioService.Players.JoinAsync<QueuedLavalinkPlayer, QueuedLavalinkPlayerOptions>(guild.Id, unit.TargetVoiceChannelId, PlayerFactory.Queued, Options.Create(new QueuedLavalinkPlayerOptions { HistoryCapacity = 100 }), cancellationToken: ct);
                if (player != null) {
                    await ApplyNormalizerFilters(player);
                }
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
