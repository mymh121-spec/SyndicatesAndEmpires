using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Game.Application.Session;
using Game.Application.World;
using Game.Domain.World;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Presentation
{
    public sealed partial class GameModeSelectionController
    {
        private SinglePlayerSave _saveLog;
        private SinglePlayerSaveStore _saveStore;
        private SinglePlayerSave _manualSave;
        private SinglePlayerSave _autoSave;
        private Button _continueManualButton;
        private Button _continueAutoButton;
        private Button _manualSaveButton;
        private Label _saveMenuStatus;
        private Label _saveStatus;
        private Label _saveHudStatus;
        private VisualElement _saveLoadOverlay;
        private Label _saveLoadProgress;
        private bool _isRestoringSave;
        private bool _autosaveAfterAdvance;
        private int _recordDepth;
        private Task _saveTail = Task.CompletedTask;

        private void OnApplicationPause(bool paused)
        {
            if (paused) _ = SaveSinglePlayerAsync("auto");
        }

        private void OnApplicationQuit()
        {
            // Desktop writes finish synchronously. Browser tab close is only
            // best effort: committed daily/manual saves are the recovery point.
            _ = SaveSinglePlayerAsync("auto");
        }

        private SinglePlayerSaveStore SaveStore
        {
            get
            {
                if (_saveStore != null) return _saveStore;
#if UNITY_WEBGL && !UNITY_EDITOR
                var bridgeRoot = new GameObject("SinglePlayerStorage-" + GetInstanceID());
                bridgeRoot.transform.SetParent(transform, false);
                _saveStore = new SinglePlayerSaveStore(bridgeRoot.AddComponent<SinglePlayerWebStorage>());
#else
                _saveStore = new SinglePlayerSaveStore(new SinglePlayerFileStorage(
                    Path.Combine(UnityEngine.Application.persistentDataPath, "SinglePlayerSaves")));
#endif
                return _saveStore;
            }
        }

        private void BuildContinueButtons()
        {
            _continueAutoButton = AddButton(_modeView, "이어하기 · 자동 저장", () =>
                _ = ContinueSinglePlayerAsync(_autoSave));
            _continueAutoButton.name = "continue-auto-save";
            _continueManualButton = AddButton(_modeView, "이어하기 · 수동 저장", () =>
                _ = ContinueSinglePlayerAsync(_manualSave));
            _continueManualButton.name = "continue-manual-save";
            _saveMenuStatus = AddStatus(_modeView);
            _saveMenuStatus.name = "save-menu-status";
            _continueAutoButton.SetEnabled(false);
            _continueManualButton.SetEnabled(false);
            _ = RefreshSaveMenuAsync();
        }

        private async Task RefreshSaveMenuAsync()
        {
            if (_saveMenuStatus == null) return;
            _saveMenuStatus.text = "저장 확인 중…";
            string errors = string.Empty;
            try { _autoSave = await SaveStore.LoadAsync("auto"); }
            catch (Exception error) { _autoSave = null; errors += "자동 저장: " + error.Message + "\n"; }
            try { _manualSave = await SaveStore.LoadAsync("manual"); }
            catch (Exception error) { _manualSave = null; errors += "수동 저장: " + error.Message; }
            if (this == null) return;
            _continueAutoButton.SetEnabled(_autoSave != null);
            _continueManualButton.SetEnabled(_manualSave != null);
            _continueAutoButton.text = SaveLabel("자동 저장", _autoSave);
            _continueManualButton.text = SaveLabel("수동 저장", _manualSave);
            _saveMenuStatus.text = !string.IsNullOrEmpty(errors) ? errors :
                _autoSave == null && _manualSave == null ? "저장된 싱글플레이가 없습니다." :
                "불러오면 일시정지 상태로 시작합니다. 저장은 이 기기·브라우저에 보관됩니다.";
        }

        private static string SaveLabel(string slot, SinglePlayerSave save) => save == null
            ? "이어하기 · " + slot + " 없음"
            : "이어하기 · " + slot + " · " + save.turn + "일차";

        private void BeginSinglePlayerRecording(MapGenerationSettings settings)
        {
            _queuedOperationId = string.Empty;
            _hasPendingDelegatedMission = false;
            _pendingRecruitmentOrigin = null;
            _selectedOperationIndex = _selectedOperationAgentIndex = _selectedOperationApproachIndex = 0;
            _pendingCommanderIndex = 0;
            _autosaveAfterAdvance = false;
            ResetCampaignHudTracking();
            if (_isRestoringSave) return;
            _saveLog = new SinglePlayerSave
            {
                size = settings.SizePreset, resources = settings.ResourceAbundance,
                water = settings.WaterLevel, seed = settings.Seed,
                neutralCastles = settings.NeutralCastleCount, wrap = settings.WrapHorizontally,
                configuration = singlePlayerSimulation.SaveConfiguration()
            };
            SetSaveStatus("매일 자동 저장 · Esc 메뉴에서 수동 저장");
        }

        private SinglePlayerInput CaptureSinglePlayerInput()
        {
            MapCellSelection? cell = gameplayMap?.CurrentSelection;
            return new SinglePlayerInput
            {
                unitId = gameplayMap?.SelectedPlayerUnitId, hasCell = cell.HasValue,
                x = cell?.Coordinate.X ?? 0, y = cell?.Coordinate.Y ?? 0,
                targetUnitId = cell?.UnitId,
                trackedEnemyUnitId = gameplayMap?.TrackedEnemyUnitId,
                archetype = _pendingUnitArchetype, weapon = _pendingWeaponType, armor = _pendingArmorClass,
                hasRecruitmentOrigin = _pendingRecruitmentOrigin.HasValue,
                recruitmentX = _pendingRecruitmentOrigin?.X ?? 0,
                recruitmentY = _pendingRecruitmentOrigin?.Y ?? 0,
                commanderIndex = _pendingCommanderIndex,
                operationIndex = _selectedOperationIndex,
                operationAgentIndex = _selectedOperationAgentIndex,
                operationApproachIndex = _selectedOperationApproachIndex
            };
        }

        private void ApplySinglePlayerInput(SinglePlayerInput input)
        {
            gameplayMap.RestoreSaveSelection(input.unitId, input.hasCell, input.x, input.y,
                input.targetUnitId, input.trackedEnemyUnitId);
            _pendingUnitArchetype = input.archetype;
            _pendingWeaponType = input.weapon;
            _pendingArmorClass = input.armor;
            _pendingRecruitmentOrigin = input.hasRecruitmentOrigin
                ? new GridCoordinate(input.recruitmentX, input.recruitmentY) : (GridCoordinate?)null;
            _pendingCommanderIndex = input.commanderIndex;
            _selectedOperationIndex = input.operationIndex;
            _selectedOperationAgentIndex = input.operationAgentIndex;
            _selectedOperationApproachIndex = input.operationApproachIndex;
        }

        private IDisposable RecordSinglePlayerAction(SinglePlayerAction action, int argument = 0)
        {
            if (!_isRestoringSave && _selection.IsSinglePlayer && _saveLog != null && _recordDepth == 0)
            {
                _saveLog.records.Add(new SinglePlayerRecord
                {
                    action = action, argument = argument, input = CaptureSinglePlayerInput()
                });
            }
            _recordDepth++;
            return new SaveActionScope(this);
        }

        private sealed class SaveActionScope : IDisposable
        {
            private readonly GameModeSelectionController _owner;
            public SaveActionScope(GameModeSelectionController owner) { _owner = owner; }
            public void Dispose() { _owner._recordDepth--; }
        }

        private void RecordRealtimeAdvance(int steps, int days, double hours)
        {
            if (_isRestoringSave || !_selection.IsSinglePlayer || _saveLog == null) return;
            _saveLog.AddAdvance(steps, days, hours);
            _autosaveAfterAdvance |= days > 0;
        }

        private void CompleteRecordedAdvance()
        {
            if (!_autosaveAfterAdvance || _isRestoringSave) return;
            _autosaveAfterAdvance = false;
            _ = SaveSinglePlayerAsync("auto");
        }

        private SinglePlayerSave CaptureSinglePlayerSave()
        {
            if (_saveLog == null || !_selection.IsSinglePlayer || _isRestoringSave)
                throw new InvalidOperationException("저장할 싱글플레이가 없습니다.");
            _saveLog.savedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            _saveLog.turn = singlePlayerSimulation.CurrentTurn.Value;
            _saveLog.playerCash = singlePlayerSimulation.PlayerCash.ToString(CultureInfo.InvariantCulture);
            _saveLog.clock = singlePlayerSimulation.CaptureClock();
            _saveLog.selection = CaptureSinglePlayerInput();
            Vector3 focus = gameplayMap.SaveCameraFocus;
            _saveLog.cameraX = focus.x;
            _saveLog.cameraZ = focus.z;
            _saveLog.cameraZoom = gameplayMap.SaveCameraZoom;
            return _saveLog;
        }

        private Task<bool> SaveSinglePlayerAsync(string slot)
        {
            if (_isRestoringSave || !_selection.IsSinglePlayer || _saveLog == null)
                return Task.FromResult(false);
            try
            {
                string frozen = SinglePlayerSaveStore.Encode(CaptureSinglePlayerSave());
                Task<bool> next = PersistSinglePlayerAsync(_saveTail, slot, frozen);
                _saveTail = next;
                return next;
            }
            catch (Exception error)
            {
                SetSaveStatus("저장 실패: " + error.Message);
                return Task.FromResult(false);
            }
        }

        private async Task<bool> PersistSinglePlayerAsync(Task previous, string slot, string frozen)
        {
            await previous;
            if (this != null) SetSaveStatus("저장 중…");
            try
            {
                await SaveStore.SaveEncodedAsync(slot, frozen);
                if (this == null) return true;
                SetSaveStatus((slot == "manual" ? "수동" : "자동") + " 저장 완료");
                await RefreshSaveMenuAsync();
                return true;
            }
            catch (Exception error)
            {
                if (this != null)
                    SetSaveStatus("저장 실패: " + error.Message + " 기존 저장은 유지됩니다.");
                return false;
            }
        }

        private void BuildSavePauseControls()
        {
            _manualSaveButton = AddButton(_pauseMenuView, "싱글플레이 저장", () =>
                _ = SaveSinglePlayerAsync("manual"));
            _manualSaveButton.name = "save-single-player";
            _saveStatus = AddStatus(_pauseMenuView);
            _saveStatus.name = "single-player-save-status";
        }

        private void BuildSaveHudStatus()
        {
            _saveHudStatus = AddStatus(_singlePlayerView);
            _saveHudStatus.name = "save-hud-status";
            _saveHudStatus.style.fontSize = 12;
        }

        private void SetSaveStatus(string message)
        {
            if (_saveStatus != null) _saveStatus.text = message;
            if (_saveHudStatus != null) _saveHudStatus.text = message;
        }

        private void ShowSaveLoading(int done, int total)
        {
            if (_saveLoadOverlay == null)
            {
                _saveLoadOverlay = new VisualElement { name = "save-loading-overlay" };
                _saveLoadOverlay.style.position = Position.Absolute;
                _saveLoadOverlay.style.left = _saveLoadOverlay.style.top = 0;
                _saveLoadOverlay.style.right = _saveLoadOverlay.style.bottom = 0;
                _saveLoadOverlay.style.backgroundColor = new Color(0.025f, 0.035f, 0.055f, 0.96f);
                _saveLoadOverlay.style.alignItems = Align.Center;
                _saveLoadOverlay.style.justifyContent = Justify.Center;
                _saveLoadProgress = AddStatus(_saveLoadOverlay);
                _uiRoot.Add(_saveLoadOverlay);
            }
            _saveLoadProgress.text = $"저장 불러오는 중… {done * 100 / Math.Max(1, total)}%";
            SetVisible(_saveLoadOverlay, true);
            _saveLoadOverlay.BringToFront();
        }

        private async Task<bool> ContinueSinglePlayerAsync(SinglePlayerSave save)
        {
            if (save == null || _isRestoringSave) return false;
            _isRestoringSave = true;
            try
            {
                save.Validate();
                _mapSizeField.value = save.size;
                _mapResourceField.value = save.resources;
                _mapWaterField.value = save.water;
                _mapSeedField.value = save.seed;
                _neutralCastleField.value = save.neutralCastles;
                _mapWrapField.value = save.wrap;
                SelectSinglePlayer();
                singlePlayerSimulation.enabled = false;
                gameplayMap.enabled = false;
                gameplayMap.PointerSelectionBlocked = true;
                gameplayMap.SetSaveReplayMode(true);
                if (singlePlayerSimulation.SaveConfiguration() != save.configuration)
                    throw new InvalidOperationException("게임 설정이 달라 이 저장을 복원할 수 없습니다.");
                ShowSaveLoading(0, save.records.Count);
                await Task.Yield();
                var slice = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < save.records.Count; i++)
                {
                    if (this == null) return false;
                    ReplaySinglePlayerRecord(save.records[i]);
                    if (slice.ElapsedMilliseconds >= 8)
                    {
                        ShowSaveLoading(i + 1, save.records.Count);
                        await Task.Yield();
                        slice.Restart();
                    }
                }
                if (singlePlayerSimulation.CurrentTurn.Value != save.turn ||
                    singlePlayerSimulation.PlayerCash.ToString(CultureInfo.InvariantCulture) != save.playerCash)
                    throw new InvalidOperationException("저장된 진행 상태와 복원 결과가 일치하지 않습니다.");
                singlePlayerSimulation.RestoreClock(save.clock, true);
                ApplySinglePlayerInput(save.selection);
                gameplayMap.RestoreSaveCamera(save.cameraX, save.cameraZ, save.cameraZoom);
                // Copy before appending: menu metadata must remain an immutable save.
                _saveLog = SinglePlayerSaveStore.Decode(SinglePlayerSaveStore.Encode(save));
                RefreshSinglePlayerStatus();
                RefreshSelectedMapActions();
                if (singlePlayerSimulation.IsCampaignFinished) ShowSinglePlayerResult();
                SetSaveStatus("저장을 불러왔습니다. Space 또는 배속 버튼으로 재개하세요.");
                return true;
            }
            catch (Exception error)
            {
                _saveLog = null;
                ShowModeSelection();
                _saveMenuStatus.text = "불러오기 실패: " + error.Message + " 저장 파일은 유지됩니다.";
                return false;
            }
            finally
            {
                _isRestoringSave = false;
                if (this != null)
                {
                    if (singlePlayerSimulation != null) singlePlayerSimulation.enabled = true;
                    if (gameplayMap != null)
                    {
                        gameplayMap.SetSaveReplayMode(false);
                        gameplayMap.enabled = true;
                        gameplayMap.PointerSelectionBlocked = false;
                    }
                    SetVisible(_saveLoadOverlay, false);
                }
            }
        }

        private void ReplaySinglePlayerRecord(SinglePlayerRecord record)
        {
            if (record.action == SinglePlayerAction.Advance)
            {
                singlePlayerSimulation.ReplayRealtimeAdvance(record.mapSteps, record.completedDays, record.gameHours);
                return;
            }
            ApplySinglePlayerInput(record.input);
            switch (record.action)
            {
                case SinglePlayerAction.Recruit: CreatePlayerUnit(); break;
                case SinglePlayerAction.Equip: EquipSelectedPlayerUnit(); break;
                case SinglePlayerAction.HireCommander: HirePendingCommander(); break;
                case SinglePlayerAction.Move: MoveSelectedPlayerUnit(); break;
                case SinglePlayerAction.Attack: TrackAndAttackSelectedEnemyUnit(); break;
                case SinglePlayerAction.CancelMove: CancelSelectedPlayerUnitMove(); break;
                case SinglePlayerAction.CaptureMine: CaptureSelectedMine(); break;
                case SinglePlayerAction.Survey: SurveySelectedEconomicSite(); break;
                case SinglePlayerAction.BuildMine: StartSelectedMineConstruction(); break;
                case SinglePlayerAction.OccupationPolicy:
                    SetSelectedOccupationPolicy((MapOccupationPolicy)record.argument); break;
                case SinglePlayerAction.SiegeAction: CycleSelectedSiegeAction(); break;
                case SinglePlayerAction.CaptureCastle: CaptureOrSiegeSelectedCastle(); break;
                case SinglePlayerAction.CastleRole: CycleSelectedCastleRole(); break;
                case SinglePlayerAction.SupplyMission:
                    AssignSelectedSupplyMission((MapSupplyMissionKind)record.argument); break;
                case SinglePlayerAction.EconomicOperation: QueueSelectedOperation(); break;
                default: throw new InvalidOperationException("지원하지 않는 저장 행동입니다.");
            }
        }
    }
}
