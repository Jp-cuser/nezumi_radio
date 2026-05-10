using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

        [SlashCommand("radio_volume", "ユニットの音量を調整します（1〜100）")]
        public async Task SetVolumeAsync([Summary("bot", "調整するBotを選択してください"), Autocomplete(typeof(UnitAutocompleteHandler))] int unit, [Summary("volume", "音量（1〜100）")] int volume)
        {
            if (!IsAllowedGuild()) { await RespondAsync("利用不可サーバーです。", ephemeral: true); return; }
            var target = _system.Units.FirstOrDefault(u => u.Index == unit - 1);
            if (target == null) return;

            if (volume < 1) volume = 1;
            if (volume > 100) volume = 100;

            var player = await target.AudioService.Players.GetPlayerAsync(Context.Guild.Id);
            if (player != null) {
                // dynamicを使用してコンパイル時の型チェックを回避し、実行時にメソッドを呼び出す
                await ((dynamic)player).SetVolumeAsync(volume / 100f);
                await RespondAsync($"✅ ユニット {unit:D2} の音量を {volume}% に設定しました。");
            }
            else {
                await RespondAsync("❌ ユニットがVCに参加していません。", ephemeral: true);
            }
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
