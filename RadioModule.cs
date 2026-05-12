using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Reflection;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Lavalink4NET;
using Lavalink4NET.Players;
using Microsoft.Extensions.DependencyInjection;

namespace NezumiRadio
{
    public class RadioModule : InteractionModuleBase<SocketInteractionContext>
    {
        private readonly RadioSystem _system;
        public RadioModule(RadioSystem system) => _system = system;

        private bool IsAllowedGuild() => 
            Context.Guild.Id == RadioSystem.ProductionGuildId || 
            Context.Guild.Id == RadioSystem.TestGuildId;

        [SlashCommand("radio_start", "ラジオを指定のユニットで起動します")]
        public async Task StartAsync([Summary("bot", "起動するBotを選択してください"), Autocomplete(typeof(UnitAutocompleteHandler))] int unit)
        {
            if (!IsAllowedGuild()) { await RespondAsync("利用不可サーバーです。", ephemeral: true); return; }
            var target = _system.Units.FirstOrDefault(u => u.Index == unit - 1);
            if (target == null) return;
            var vc = (Context.User as IVoiceState)?.VoiceChannel;
            if (vc == null) { await RespondAsync("VCに参加してください。", ephemeral: true); return; }
            
            target.IsActive = true; 
            target.TargetVoiceChannelId = vc.Id;
            await RespondAsync($"✅ ユニット {unit:D2} を起動しました。");
            _ = _system.TriggerUnitTickAsync(unit - 1);
        }

        [SlashCommand("radio_stop", "ユニットを停止します")]
        public async Task StopAsync([Summary("bot", "停止するBotを選択してください。未指定で全停止")] int unit = 0)
        {
            if (!IsAllowedGuild()) { await RespondAsync("利用不可サーバーです。", ephemeral: true); return; }
            if (unit > 0) {
                var target = _system.Units.FirstOrDefault(u => u.Index == unit - 1);
                if (target != null) { target.IsActive = false; target.TargetVoiceChannelId = 0; }
                await RespondAsync($"✅ ユニット {unit:D2} を停止しました。");
                _ = _system.TriggerUnitTickAsync(unit - 1);
            } else {
                foreach (var u in _system.Units) { u.IsActive = false; u.TargetVoiceChannelId = 0; }
                await RespondAsync("✅ 全ユニットを停止しました。");
                foreach (var u in _system.Units) _ = _system.TriggerUnitTickAsync(u.Index);
            }
        }

        [SlashCommand("radio_volume", "ユニットの音量を調整します")]
        public async Task SetVolumeAsync(
            [Summary("unit", "1-6")] int unit,
            [Summary("volume", "1-100")] int volume)
        {
            if (unit < 1 || unit > 6) { await RespondAsync("❌ ユニット番号は1〜6で指定してください。", ephemeral: true); return; }
            var target = _system.Units.FirstOrDefault(u => u.Index == unit - 1);
            if (target == null) { await RespondAsync("❌ 指定されたユニットが見つかりません。", ephemeral: true); return; }

            var player = await target.AudioService.Players.GetPlayerAsync(Context.Guild.Id);
            if (player != null) {
                // 安全なReflectionを使用して音量を設定
                try {
                    var method = player.GetType().GetMethod("SetVolumeAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);
                    if (method != null) {
                        var result = method.Invoke(player, new object[] { volume / 100f, null! });
                        if (result is ValueTask vt) await vt;
                    }
                } catch { }
                await RespondAsync($"✅ ユニット {unit:D2} の音量を {volume}% に設定しました。");
            }
            else {
                await RespondAsync("❌ ユニットがVCに参加していません。", ephemeral: true);
            }
        }

        [SlashCommand("radio_sync", "全ユニットの再生を強制停止して再同期（ジャンル切り替え）します")]
        public async Task SyncAllAsync()
        {
            await DeferAsync();
            await _system.StopAllPlaybackAsync();
            await FollowupAsync("✅ 全ユニットの再生を停止し、再同期のリクエストを送信しました。順次ジングルから再生が再開されます。");
        }
    }

    public class UnitAutocompleteHandler : AutocompleteHandler
    {
        public override async Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context, IAutocompleteInteraction interaction, IParameterInfo parameter, IServiceProvider services)
        {
            var system = services.GetRequiredService<RadioSystem>();
            var results = new List<AutocompleteResult>();
            foreach (var u in system.Units)
            {
                if (context.Guild is not SocketGuild socketGuild) continue;
                var member = socketGuild.GetUser(u.Client.CurrentUser.Id);
                string name = member?.Nickname ?? u.Client.CurrentUser.Username;
                results.Add(new AutocompleteResult($"{name} [{(u.IsActive ? "稼働中" : "待機中")}]", u.Index + 1));
            }
            return AutocompletionResult.FromSuccess(results);
        }
    }
}
