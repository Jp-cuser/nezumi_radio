using System;
using System.Collections.Generic;
using Discord.WebSocket;
using Discord.Interactions;
using Lavalink4NET;
using Lavalink4NET.Tracks;
using System.Threading;

namespace NezumiRadio
{
    public class UnitState 
    { 
        public bool IsActive { get; set; } 
        public ulong TargetVoiceChannelId { get; set; } 
    }

    public class SystemState 
    { 
        public Dictionary<int, UnitState> Units { get; set; } = new(); 
    }

    public class BotUnit : IDisposable
    {
        public int Index { get; set; }
        public DiscordSocketClient Client { get; set; } = null!;
        public IAudioService AudioService { get; set; } = null!;
        public InteractionService InteractionService { get; set; } = null!;
        public bool IsActive { get; set; }
        public ulong TargetVoiceChannelId { get; set; }
        public string LastGenre { get; set; } = string.Empty;
        public string CurrentGenreKatakana { get; set; } = string.Empty;
        public string CurrentTrackTitle { get; set; } = string.Empty;
        
        public List<string> UrlPool { get; set; } = new();
        public List<LavalinkTrack> VirtualQueue { get; set; } = new();
        public List<string> NextUrlPool { get; set; } = new();
        
        public bool IsPreloading { get; set; }
        public int CurrentTrackIndex { get; set; }
        public DateTime TrackStartTime { get; set; }
        public SemaphoreSlim Lock { get; } = new(1, 1);
        
        public void Dispose() 
        { 
            Client?.Dispose(); 
            Lock?.Dispose(); 
        }
    }
}
