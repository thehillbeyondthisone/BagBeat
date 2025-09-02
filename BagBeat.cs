using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Generic;

using AOSharp.Core;
using AOSharp.Core.UI;
using AOSharp.Core.Inventory;

namespace BagBeat
{
    // AOSharp plugin with fade-aware patterns (C# 7.3 safe & cleaned)
    public class Main : AOPluginEntry
    {
        private enum BeatMode { All = 0, Chase = 1, Strobe = 2, Breathe = 3 }

        // Core state
        private static bool _running;
        private static volatile int _bpm = 120;
        private static double _accumMs;
        private static int _pulseRemaining;

        // Patterns / fade fun
        private static BeatMode _mode = BeatMode.All;
        private static int _fadeGuessMs = 2500; // ms; tune to your client’s dim falloff
        private static int _chaseIndex;
        private static int _halfBeatCount; // half-beat counter

        // Tiny scheduler (runs in OnUpdate)
        private class ScheduledAction { public long DueMs; public Action Fn; }
        private static readonly List<ScheduledAction> _queue = new List<ScheduledAction>();
        private static DateTime _t0;

        // UDP sync
        private static bool _syncEnabled;
        private static Thread _udpThread;
        private static CancellationTokenSource _udpCts;
        private const int SyncPort = 45454;

        // Tap tempo
        private static readonly object _tapLock = new object();
        private static DateTime _lastTap = DateTime.MinValue;
        private static readonly double[] _intervals = new double[8];
        private static int _intervalCount;

        // Some SDKs still call the legacy Run(string). Marking obsolete to match base.
        [Obsolete("AOSharp SDK legacy entrypoint. Newer SDKs may call Run() without args.")]
        public override void Run(string pluginDir) { Init(); }

        public override void Run() { Init(); }

        private static void Init()
        {
            _t0 = DateTime.UtcNow;
            Chat.WriteLine("<BagBeat> Commands: /bagbeat on | off | bpm <30-600> | once | tap | sync <on|off> | mode <all|chase|strobe|breathe> | fade <ms>");
            Game.OnUpdate += OnUpdate;

            // Simplified lambda (type inference); 3-arg signature matches this AOSharp fork
            Chat.RegisterCommand("bagbeat", (command, args, chat) =>
            {
                if (args.Length == 0) { Help(); return; }

                var sub = args[0].ToLowerInvariant();
                switch (sub)
                {
                    case "on":
                        _running = true;
                        _accumMs = 0;
                        _halfBeatCount = 0;
                        Chat.WriteLine("<BagBeat> started at " + _bpm + " BPM. Mode=" + _mode);
                        break;

                    case "off":
                        _running = false;
                        Chat.WriteLine("<BagBeat> stopped.");
                        break;

                    case "bpm":
                        if (args.Length >= 2 && int.TryParse(args[1], out var val))
                        {
                            SetBpm(val);
                            Chat.WriteLine("<BagBeat> BPM set to " + _bpm + ".");
                        }
                        else Chat.WriteLine("<BagBeat> Usage: /bagbeat bpm <30-600>");
                        break;

                    case "once":
                        _pulseRemaining = 2;       // close -> open
                        _accumMs = 1e9;            // fire on next update
                        break;

                    case "tap":
                        Tap();
                        break;

                    case "sync":
                        if (args.Length >= 2)
                        {
                            var onoff = args[1].ToLowerInvariant();
                            if (onoff == "on")
                            {
                                EnsureUdpListener();
                                _syncEnabled = true;
                                Chat.WriteLine("<BagBeat> Sync ON (UDP :" + SyncPort + ").");
                            }
                            else if (onoff == "off")
                            {
                                _syncEnabled = false;
                                Chat.WriteLine("<BagBeat> Sync OFF.");
                            }
                            else Chat.WriteLine("<BagBeat> Usage: /bagbeat sync <on|off>");
                        }
                        else Chat.WriteLine("<BagBeat> Usage: /bagbeat sync <on|off>");
                        break;

                    case "mode":
                        if (args.Length >= 2)
                        {
                            var m = args[1].ToLowerInvariant();
                            if (m == "all") _mode = BeatMode.All;
                            else if (m == "chase") _mode = BeatMode.Chase;
                            else if (m == "strobe") _mode = BeatMode.Strobe;
                            else if (m == "breathe") _mode = BeatMode.Breathe;
                            else { Chat.WriteLine("<BagBeat> Usage: /bagbeat mode <all|chase|strobe|breathe>"); break; }

                            _chaseIndex = 0;
                            _halfBeatCount = 0;
                            Chat.WriteLine("<BagBeat> Mode = " + _mode);
                        }
                        else Chat.WriteLine("<BagBeat> Usage: /bagbeat mode <all|chase|strobe|breathe>");
                        break;

                    case "fade":
                        if (args.Length >= 2 && int.TryParse(args[1], out var ms) && ms >= 500 && ms <= 8000)
                        {
                            _fadeGuessMs = ms;
                            Chat.WriteLine("<BagBeat> Fade hint set to " + _fadeGuessMs + " ms.");
                        }
                        else Chat.WriteLine("<BagBeat> Usage: /bagbeat fade <500-8000>");
                        break;

                    default:
                        Help();
                        break;
                }
            });
        }

        public override void Teardown()
        {
            _running = false;
            Game.OnUpdate -= OnUpdate;
            StopUdpListener();
        }

        private static void Help()
        {
            Chat.WriteLine("<BagBeat> /bagbeat on | off | bpm <30-600> | once | tap | sync <on|off> | mode <all|chase|strobe|breathe> | fade <ms>");
        }

        private static void SetBpm(int bpm)
        {
            if (bpm < 30) bpm = 30;
            if (bpm > 600) bpm = 600;
            _bpm = bpm;
        }

        private static void OnUpdate(object s, float deltaTime)
        {
            // run due scheduled actions
            var nowMs = (long)(DateTime.UtcNow - _t0).TotalMilliseconds;
            for (var i = _queue.Count - 1; i >= 0; i--)
            {
                if (_queue[i].DueMs > nowMs) continue;
                try { _queue[i].Fn(); } catch { }
                _queue.RemoveAt(i);
            }

            _accumMs += deltaTime * 1000.0;
            var halfIntervalMs = 60000.0 / ((double)Math.Max(30, _bpm) * 2.0);

            if (_accumMs < halfIntervalMs) return;
            _accumMs = 0;
            _halfBeatCount++;

            if (_pulseRemaining > 0)
            {
                ToggleAllBackpacks();
                _pulseRemaining--;
                return;
            }

            if (!_running) return;

            // Pattern engine
            switch (_mode)
            {
                case BeatMode.All:
                    ToggleAllBackpacks();
                    break;

                case BeatMode.Strobe:
                    // Quick close->open to reset “dim” timers on all
                    PulseAll(0, 80);
                    break;

                case BeatMode.Chase:
                    {
                        var bags = SafeBags();
                        if (bags.Count == 0) return;

                        // Keep most bright with periodic refresh
                        if ((_halfBeatCount % 8) == 0) PulseAll(0, 80);

                        var bag = bags[_chaseIndex % bags.Count];
                        PokeBag(bag, 0, 90); // close then open shortly after
                        _chaseIndex++;
                        break;
                    }

                case BeatMode.Breathe:
                    {
                        // Downbeat brighten; let them fall off between refreshes
                        var fullBeat = _halfBeatCount / 2;
                        if ((fullBeat % 4) == 0)
                        {
                            PulseAll(0, 80);
                        }
                        else
                        {
                            // If fade is short, give a gentle mid-cycle nudge
                            if ((_halfBeatCount % 4) == 2 && _fadeGuessMs < 2000)
                                PulseAll(0, 80);
                        }
                        break;
                    }
            }
        }

        // -------- bag actions --------

        private static List<Backpack> SafeBags()
        {
            try { return Inventory.Backpacks.ToList(); }
            catch { return new List<Backpack>(); }
        }

        private static void ToggleAllBackpacks()
        {
            try
            {
                foreach (var b in SafeBags())
                {
                    var slot = b.Slot; // capture per-iteration
                    try { Item.Use(slot); } catch { }
                }
            }
            catch (Exception ex)
            {
                Chat.WriteLine("<BagBeat> error: " + ex.Message);
                _running = false;
            }
        }

        private static void PulseAll(int closeDelayMs, int openDelayMs)
        {
            var bags = SafeBags();
            var nowMs = (long)(DateTime.UtcNow - _t0).TotalMilliseconds;

            foreach (var b in bags)
            {
                var slot = b.Slot; // capture
                Schedule(nowMs + closeDelayMs, () => { try { Item.Use(slot); } catch { } });
                Schedule(nowMs + closeDelayMs + openDelayMs, () => { try { Item.Use(slot); } catch { } });
            }
        }

        private static void PokeBag(Backpack bag, int closeDelayMs, int openDelayMs)
        {
            var nowMs = (long)(DateTime.UtcNow - _t0).TotalMilliseconds;
            var slot = bag.Slot;
            Schedule(nowMs + closeDelayMs, () => { try { Item.Use(slot); } catch { } });
            Schedule(nowMs + closeDelayMs + openDelayMs, () => { try { Item.Use(slot); } catch { } });
        }

        private static void Schedule(long dueMs, Action fn)
        {
            _queue.Add(new ScheduledAction { DueMs = dueMs, Fn = fn });
        }

        // -------- tap tempo --------
        private static void Tap()
        {
            lock (_tapLock)
            {
                var now = DateTime.UtcNow;
                if (_lastTap != DateTime.MinValue)
                {
                    var ms = (now - _lastTap).TotalMilliseconds;
                    if (ms >= 120 && ms <= 2000)
                    {
                        if (_intervalCount < _intervals.Length)
                            _intervals[_intervalCount++] = ms;
                        else
                        {
                            for (var i = 1; i < _intervals.Length; i++)
                                _intervals[i - 1] = _intervals[i];
                            _intervals[_intervals.Length - 1] = ms;
                        }

                        double sum = 0;
                        for (var i = 0; i < _intervalCount; i++) sum += _intervals[i];
                        var avgMs = (_intervalCount > 0) ? (sum / _intervalCount) : ms;

                        var bpm = (int)Math.Round(60000.0 / avgMs);
                        SetBpm(bpm);
                        Chat.WriteLine("<BagBeat> TAP → " + _bpm + " BPM");
                    }
                    else _intervalCount = 0;
                }
                _lastTap = now;
            }
        }

        // -------- UDP sync (explicit ThreadStart to avoid ambiguity) --------
        private static void EnsureUdpListener()
        {
            if (_udpThread != null) return;

            _udpCts = new CancellationTokenSource();
            var ts = new ThreadStart(UdpThreadProc);
            _udpThread = new Thread(ts) { IsBackground = true, Name = "BagBeatSync" };
            _udpThread.Start();
        }

        private static void UdpThreadProc()
        {
            var token = _udpCts.Token;
            UdpLoop(token);
        }

        private static void StopUdpListener()
        {
            try { _udpCts?.Cancel(); _udpThread = null; } catch { }
        }

        private static void UdpLoop(CancellationToken token)
        {
            UdpClient udp = null;
            try
            {
                udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, SyncPort));
                udp.Client.ReceiveTimeout = 1000;
                var any = new IPEndPoint(IPAddress.Any, 0);

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var data = udp.Receive(ref any);
                        var text = Encoding.ASCII.GetString(data).Trim();

                        int bpmVal;
                        if (text.StartsWith("BPM:", StringComparison.OrdinalIgnoreCase))
                        {
                            var payload = text.Substring(4).Trim();
                            if (int.TryParse(payload, out bpmVal) && _syncEnabled) SetBpm(bpmVal);
                        }
                        else if (int.TryParse(text, out bpmVal) && _syncEnabled)
                        {
                            SetBpm(bpmVal);
                        }
                    }
                    catch (SocketException se)
                    {
                        if (se.SocketErrorCode != SocketError.TimedOut) Thread.Sleep(10);
                    }
                    catch { }
                }
            }
            finally
            {
                try { udp?.Close(); } catch { }
            }
        }
    }
}
