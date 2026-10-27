using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Game.Application.Session;
using Game.Domain.World;
using Game.Presentation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Game.Tests.Playability
{
    public sealed class SinglePlayerSaveTests
    {
        private sealed class MemoryStorage : ISinglePlayerSaveStorage
        {
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
            public bool FailWrites;
            public Task<string> ReadAsync(string key) => Task.FromResult(
                Values.TryGetValue(key, out string value) ? value : null);
            public Task WriteAsync(string key, string value)
            {
                if (FailWrites) throw new IOException("test storage full");
                Values[key] = value;
                return Task.CompletedTask;
            }
        }

        private sealed class Session : IDisposable
        {
            public readonly GameModeSelectionController Controller;
            public SimulationBootstrapper Simulation => Field<SimulationBootstrapper>(Controller, "singlePlayerSimulation");
            public StarterMapController Map => Field<StarterMapController>(Controller, "gameplayMap");
            private readonly GameObject _root;
            public Session(MemoryStorage storage)
            {
                _root = new GameObject("Save tests");
                Controller = _root.AddComponent<GameModeSelectionController>();
                Set(Controller, "_saveStore", new SinglePlayerSaveStore(storage));
                Invoke(Controller, "Start");
            }
            public void Start() { Invoke(Controller, "SelectSinglePlayer"); }
            public void Advance(int count, int speed = 4)
            {
                Simulation.SetRealtimeSpeed(speed);
                for (int i = 0; i < count && !Simulation.IsCampaignFinished; i++)
                    Simulation.AdvanceRealtime(1.5);
            }
            public Task<bool> Save(string slot) => (Task<bool>)Invoke(Controller, "SaveSinglePlayerAsync", slot);
            public Task<bool> Load(SinglePlayerSave save) => (Task<bool>)Invoke(Controller, "ContinueSinglePlayerAsync", save);
            public void Dispose()
            {
                var simulation = Simulation;
                var map = Map;
                Object.DestroyImmediate(_root);
                if (simulation != null) Object.DestroyImmediate(simulation.gameObject);
                if (map != null) Object.DestroyImmediate(map.gameObject);
            }
        }

        [UnityTest]
        public IEnumerator Save_RecreatesWorldOrdersEconomyAndDeterministicFuture()
        {
            var storage = new MemoryStorage();
            SinglePlayerSave save;
            string expected;
            string future;
            using (var first = new Session(storage))
            {
                first.Start();
                Invoke(first.Controller, "CreatePlayerUnit");
                Assert.That(first.Map.GameplayService.Units.Count, Is.GreaterThan(4));
                Invoke(first.Controller, "HirePendingCommander");
                first.Advance(25);
                Assert.That(storage.Values.ContainsKey("auto"), Is.True, "Midnight must commit an autosave");
                for (int i = 0; i < 20 &&
                    Invoke(first.Controller, "GetSelectedOfferedOperation") == null; i++)
                    first.Advance(10);
                Assert.That(Invoke(first.Controller, "GetSelectedOfferedOperation"), Is.Not.Null);

                GridCoordinate origin = first.Map.SelectedPlayerUnit.Coordinate;
                GridCoordinate destination = first.Map.CurrentLayout.Move(origin, 1, 0);
                if (!first.Map.CanMoveSelectedPlayerUnit(destination, out _))
                    destination = first.Map.CurrentLayout.Move(origin, 0, 1);
                Assert.That(first.Map.CanMoveSelectedPlayerUnit(destination, out _), Is.True);
                first.Map.TrySelectMapCell(destination, out _);
                Invoke(first.Controller, "MoveSelectedPlayerUnit");
                Invoke(first.Controller, "QueueSelectedOperation");
                Assert.That(first.Simulation.QueuedCommandCount, Is.GreaterThan(0),
                    "The save must contain a real pending economic command");
                first.Simulation.SetRealtimeSpeed(0);
                var saved = first.Save("manual");
                while (!saved.IsCompleted) yield return null;
                Assert.That(saved.Result, Is.True);
                save = SinglePlayerSaveStore.Decode(storage.Values["manual"]);
                Assert.That(save.records.Any(r => r.action == SinglePlayerAction.Recruit), Is.True);
                Assert.That(save.records.Count, Is.LessThan(250), "Clock records should be batched");
                expected = WorldFingerprint(first);
                first.Advance(40);
                future = WorldFingerprint(first);
            }
            using (var restored = new Session(storage))
            {
                Task<bool> loaded = restored.Load(save);
                while (!loaded.IsCompleted) yield return null;
                Assert.That(loaded.Result, Is.True,
                    Field<Label>(restored.Controller, "_saveMenuStatus").text);
                Assert.That(restored.Simulation.IsRealtimePaused, Is.True);
                Assert.That(restored.Map.SelectedPlayerUnitId, Is.EqualTo(save.selection.unitId));
                Assert.That(restored.Map.TrackedEnemyUnitId, Is.EqualTo(save.selection.trackedEnemyUnitId));
                Assert.That(restored.Map.CurrentSelection.Value.Coordinate,
                    Is.EqualTo(new GridCoordinate(save.selection.x, save.selection.y)));
                Assert.That(restored.Simulation.CaptureClock().totalGameHours,
                    Is.EqualTo(save.clock.totalGameHours));
                Assert.That(WorldFingerprint(restored), Is.EqualTo(expected));
                restored.Advance(40);
                Assert.That(WorldFingerprint(restored), Is.EqualTo(future),
                    "A restored world must produce the same future, including AI and queued orders");
            }
        }

        [UnityTest]
        public IEnumerator Saves_KeepManualSeparateAndRecoverPreviousValidCopy()
        {
            var storage = new MemoryStorage();
            using (var session = new Session(storage))
            {
                session.Start();
                session.Advance(12);
                Task<bool> first = session.Save("manual");
                while (!first.IsCompleted) yield return null;
                Assert.That(first.Result, Is.True);
                string previous = storage.Values["manual"];
                session.Advance(12);
                Assert.That(storage.Values["manual"], Is.EqualTo(previous));
                Task<bool> second = session.Save("manual");
                while (!second.IsCompleted) yield return null;
                Assert.That(storage.Values["manual.backup"], Is.EqualTo(previous));
                storage.Values["manual"] = "{truncated";
                Task<SinglePlayerSave> recovered = new SinglePlayerSaveStore(storage).LoadAsync("manual");
                while (!recovered.IsCompleted) yield return null;
                Assert.That(recovered.Result.turn, Is.EqualTo(SinglePlayerSaveStore.Decode(previous).turn));
                storage.FailWrites = true;
                Task<bool> failed = session.Save("manual");
                while (!failed.IsCompleted) yield return null;
                Assert.That(failed.Result, Is.False);
                Assert.That(storage.Values["manual.backup"], Is.EqualTo(previous));
            }
        }

        [UnityTest]
        public IEnumerator NewGameAndMultiplayer_DoNotSaveIntoManualSlot()
        {
            var storage = new MemoryStorage();
            using (var session = new Session(storage))
            {
                session.Start();
                session.Advance(12);
                Task<bool> saved = session.Save("manual");
                while (!saved.IsCompleted) yield return null;
                string manual = storage.Values["manual"];
                Invoke(session.Controller, "ShowModeSelection");
                session.Start();
                Assert.That(session.Simulation.CurrentTurn.Value, Is.EqualTo(1));
                Assert.That(session.Simulation.PlayerCash, Is.EqualTo(500000m));
                Assert.That(Field<SinglePlayerSave>(session.Controller, "_saveLog").records, Is.Empty);
                session.Advance(12);
                Assert.That(storage.Values["manual"], Is.EqualTo(manual));
                Invoke(session.Controller, "SelectMultiplayer");
                Assert.That(session.Save("manual").Result, Is.False);
                Assert.That(storage.Values["manual"], Is.EqualTo(manual));
                var multiplayer = Field<PvpOnlineSessionController>(session.Controller, "multiplayerSession");
                if (multiplayer != null) Object.DestroyImmediate(multiplayer.gameObject);
            }
        }

        [UnityTest]
        public IEnumerator InvalidAndIncompatibleSaves_AreRejectedWithoutOverwritingStorage()
        {
            var storage = new MemoryStorage();
            using (var session = new Session(storage))
            {
                session.Start();
                Task<bool> saved = session.Save("manual");
                while (!saved.IsCompleted) yield return null;
                string original = storage.Values["manual"];
                Assert.Throws<InvalidDataException>(() =>
                    SinglePlayerSaveStore.Decode(original.Replace("checksum", "brokenChecksum")));
                SinglePlayerSave incompatible = SinglePlayerSaveStore.Decode(original);
                incompatible.version++;
                Assert.Throws<InvalidOperationException>(() => incompatible.Validate());
                incompatible = SinglePlayerSaveStore.Decode(original);
                incompatible.configuration = "changed settings";
                Task<bool> load = session.Load(incompatible);
                while (!load.IsCompleted) yield return null;
                Assert.That(load.Result, Is.False);
                Assert.That(storage.Values["manual"], Is.EqualTo(original));
                Assert.That(Field<Label>(session.Controller, "_saveMenuStatus").text, Does.Contain("불러오기 실패"));
            }
        }

        private static object Invoke(object target, string name, params object[] arguments) =>
            target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(target, arguments);
        private static T Field<T>(object target, string name) =>
            (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
        private static void Set(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);

        // Test-only structural inspection catches omitted state such as garrisons,
        // supply, private AI counters and pending orders, not just visible cash.
        private static string WorldFingerprint(Session session)
        {
            var text = new StringBuilder();
            var seen = new List<object>();
            Append(session.Simulation.CurrentWorldEconomy, text, seen);
            Append(session.Simulation.CurrentAutonomousWorld, text, seen);
            Append(session.Simulation.CurrentCampaignState, text, seen);
            Append(session.Map.GameplayService, text, seen);
            Append(session.Simulation.QueuedCommandCount, text, seen);
            Append(Field<string>(session.Controller, "_queuedOperationId"), text, seen);
            Append(Field<bool>(session.Controller, "_hasPendingDelegatedMission"), text, seen);
            return text.ToString();
        }
        private static void Append(object value, StringBuilder text, List<object> seen)
        {
            if (value == null) { text.Append("null;"); return; }
            Type type = value.GetType();
            if (value is Delegate) return;
            if (type.IsPrimitive || type.IsEnum || value is decimal || value is string)
            {
                text.Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append(';');
                return;
            }
            if (!type.IsValueType)
            {
                int index = seen.FindIndex(item => ReferenceEquals(item, value));
                if (index >= 0) { text.Append('@').Append(index).Append(';'); return; }
                seen.Add(value);
            }
            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                { Append(entry.Key, text, seen); Append(entry.Value, text, seen); }
            }
            else if (value is IEnumerable enumerable)
            {
                foreach (object item in enumerable) Append(item, text, seen);
            }
            else if (type.Namespace?.StartsWith("Game.", StringComparison.Ordinal) == true)
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .OrderBy(field => field.Name, StringComparer.Ordinal))
                {
                    text.Append(field.Name).Append('=');
                    Append(field.GetValue(value), text, seen);
                }
            }
            text.Append('|');
        }
    }
}
