using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace LineOfSight
{
    internal enum LosClientState
    {
        Idle = 0,
        Active = 1,
        Faulted = 2,
    }

    // Every client publishes what its line-of-sight pass is doing as a Photon actor property.
    // The server caches those and replays them to whoever joins next, so the report arrives
    // whichever order the GM and the player joined in. TaleSpire itself distributes
    // "ClientVersion" and "ClientMode" the same way.
    internal static class ClientStatusBeacon
    {
        // Actor properties are a flat namespace shared with the game and every other mod, so
        // the key is qualified rather than something like "LoS".
        private const string PropertyKey = "LineOfSight.Status";

        // Bumped only if the payload layout changes. A client reporting a protocol we do not
        // know still counts as having the plugin — we just cannot judge what it is doing.
        private const string Protocol = "1";

        private const float PUBLISH_INTERVAL = 1f;

        private static LosClientState _state = LosClientState.Idle;
        private static string _fault = "";
        private static bool _faultIsPermanent;
        private static float _nextPublish;

        internal static void SetActive(bool active)
        {
            // Restarting the tracking loop is a fresh attempt, so it clears a fault — but not
            // one the plugin already knows it cannot recover from.
            if (!_faultIsPermanent)
            {
                _state = active ? LosClientState.Active : LosClientState.Idle;
                _fault = "";
            }
            _nextPublish = 0f;
        }

        // Keep the text stable across repeats of the same failure. The beacon is only written
        // when its value changes, so a message carrying a zone coordinate or a counter would
        // put a Photon write on every frame that fails.
        internal static void ReportFault(string what, Exception ex = null, bool permanent = false)
        {
            // Once the real cause is known, later knock-on failures must not paper over it.
            if (_faultIsPermanent && !permanent) return;

            string text = ex == null ? what : what + ": " + ex.GetType().Name;
            if (_state == LosClientState.Faulted && _fault == text) return;

            _state = LosClientState.Faulted;
            _fault = text;
            _faultIsPermanent = permanent;
            _nextPublish = 0f;
        }

        // A pass that wrote every box it meant to clears an earlier fault, so one bad frame does
        // not leave the client looking broken to the GM for the rest of the session.
        internal static void NoteRecovered()
        {
            if (_faultIsPermanent || _state != LosClientState.Faulted) return;
            _state = LosClientState.Active;
            _fault = "";
            _nextPublish = 0f;
        }

        internal static void Publish()
        {
            if (Time.time < _nextPublish) return;
            _nextPublish = Time.time + PUBLISH_INTERVAL;

            // The field outlives the room it was set for, so being in a board is the real test —
            // an actor property written outside one has nowhere to go.
            var player = LocalClient.PhotonPlayer;
            if (player == null || !BoardSessionManager.InBoard) return;

            string want = Protocol + "|" + (int)_state + "|" + _fault;

            // Joining a board makes the client a fresh Photon actor with empty properties, so
            // the target is what the room holds right now, not what we last sent.
            if ((player.CustomProperties?[PropertyKey] as string) == want) return;

            try
            {
                player.SetCustomProperties(new Hashtable { { PropertyKey, want } });
            }
            catch (Exception ex)
            {
                LineOfSightPlugin.Log?.LogWarning($"[LineOfSight] Could not publish client status: {ex.Message}");
            }
        }

        internal static bool TryRead(PhotonPlayer player, out LosClientState state, out string fault)
        {
            state = LosClientState.Idle;
            fault = "";

            if (!(player?.CustomProperties?[PropertyKey] is string raw)) return false;

            var parts = raw.Split(new[] { '|' }, 3);
            if (parts.Length < 2) return false;
            if (parts[0] != Protocol)
            {
                // Newer plugin, unknown payload: it is installed, and that is all we can say.
                state = LosClientState.Active;
                return true;
            }

            if (!int.TryParse(parts[1], out var code)) return false;
            state = (LosClientState)code;
            if (parts.Length > 2) fault = parts[2];
            return true;
        }
    }

    // GM-side half: watch everyone else's beacon and say something when a client is not hiding
    // terrain it should be hiding. A client with no plugin sends nothing at all, so its absence
    // can only be judged on a timeout — hence the grace period, which has to outlast a slow
    // board load or the GM gets warned about players who are merely still loading.
    internal static class GmClientWatch
    {
        private const float POLL_INTERVAL = 1f;

        private class Watched
        {
            internal float NotOkSince = -1f;
            internal bool WarnedAbsent;
            internal string WarnedFault;
        }

        private static readonly Dictionary<ClientGuid, Watched> _watched = new Dictionary<ClientGuid, Watched>();
        private static readonly HashSet<ClientGuid> _present = new HashSet<ClientGuid>();
        private static float _losRequiredSince = -1f;
        private static float _nextPoll;

        internal static void Reset()
        {
            _watched.Clear();
            _losRequiredSince = -1f;
        }

        internal static void Poll(bool boardRequiresLos, bool enabled, float graceSeconds)
        {
            if (Time.time < _nextPoll) return;
            _nextPoll = Time.time + POLL_INTERVAL;

            if (!enabled || !boardRequiresLos || !LocalClient.IsInGmMode)
            {
                _losRequiredSince = -1f;
                return;
            }

            // Clients already in the board when the GM switches line of sight on get their
            // clock started here, not at the moment they joined.
            if (_losRequiredSince < 0f) _losRequiredSince = Time.time;

            List<string> complaints = null;
            _present.Clear();

            var buffer = BoardSessionManager.ClientGuidToPhotonPlayer.GetBuffer(out int count);
            for (int i = 0; i < count; i++)
            {
                var clientId = buffer[i].Item1;
                var photonPlayer = buffer[i].Item2;

                if (clientId.Equals(LocalClient.Id)) continue;

                // A GM is meant to see the whole map, so their client hides nothing and has
                // nothing to report.
                if (BoardSessionManager.ClientsModes.TryGetValue(clientId, out var mode)
                    && mode == ClientMode.GameMaster) continue;

                _present.Add(clientId);
                if (!_watched.TryGetValue(clientId, out var watched))
                    _watched[clientId] = watched = new Watched();

                bool hasPlugin = ClientStatusBeacon.TryRead(photonPlayer, out var state, out var fault);

                if (hasPlugin && state == LosClientState.Faulted)
                {
                    // A fault is something the client said, not something it failed to say,
                    // so it does not wait out the grace period.
                    // Warn once per distinct failure: a fault that clears and comes straight back
                    // is the same news, and this one is a modal.
                    if (watched.WarnedFault != fault)
                    {
                        watched.WarnedFault = fault;
                        Add(ref complaints, clientId, photonPlayer, "LineOfSight reported an error — " + fault);
                    }
                    continue;
                }

                if (hasPlugin && state == LosClientState.Active)
                {
                    watched.NotOkSince = -1f;
                    watched.WarnedAbsent = false;
                    continue;
                }

                if (watched.NotOkSince < 0f) watched.NotOkSince = Time.time;
                if (watched.WarnedAbsent) continue;
                if (Time.time < Math.Max(watched.NotOkSince, _losRequiredSince) + graceSeconds) continue;

                watched.WarnedAbsent = true;
                Add(ref complaints, clientId, photonPlayer, hasPlugin
                    ? "has LineOfSight installed but is not applying it"
                    : "does not have the LineOfSight plugin");
            }

            Prune();

            if (complaints != null) Warn(complaints);
        }

        private static void Add(ref List<string> complaints, ClientGuid clientId, PhotonPlayer photonPlayer, string what)
        {
            (complaints ?? (complaints = new List<string>())).Add(NameOf(clientId, photonPlayer) + " " + what);
        }

        private static string NameOf(ClientGuid clientId, PhotonPlayer photonPlayer)
        {
            if (BoardSessionManager.ClientsPlayerGuids.TryGetValue(clientId, out var playerId))
            {
                string name = CampaignSessionManager.GetPlayerName(playerId);
                if (!string.IsNullOrEmpty(name)) return name;
            }
            return photonPlayer?.NickName ?? "A player";
        }

        private static void Prune()
        {
            List<ClientGuid> gone = null;
            foreach (var kv in _watched)
                if (!_present.Contains(kv.Key))
                    (gone ?? (gone = new List<ClientGuid>())).Add(kv.Key);
            if (gone == null) return;
            foreach (var id in gone) _watched.Remove(id);
        }

        private static void Warn(List<string> complaints)
        {
            var body = new StringBuilder("Line of sight is on for this board, but:\n");
            foreach (var line in complaints) body.Append("\n  • ").Append(line);
            body.Append("\n\nThose clients can see terrain their creatures cannot.");

            string text = body.ToString();
            LineOfSightPlugin.Log?.LogWarning("[LineOfSight] [Watch] " + text.Replace("\n", " "));

            try
            {
                SystemMessage.SendSystemMessage("Line of sight", text, "OK", null);
            }
            catch (Exception ex)
            {
                LineOfSightPlugin.Log?.LogWarning($"[LineOfSight] [Watch] Could not show warning: {ex.Message}");
            }
        }
    }
}
