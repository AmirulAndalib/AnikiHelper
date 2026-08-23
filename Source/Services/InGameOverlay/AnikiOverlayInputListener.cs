using Playnite.SDK;
using Playnite.SDK.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using AnikiHelper.Services.WebBrowser;

namespace AnikiHelper.Services.InGameOverlay
{
    /// <summary>Routes P10 input and borrows Playnite-owned SDL handles for analog/in-game fallback.</summary>
    internal sealed class AnikiOverlayInputListener : IDisposable
    {
        private const int SDL_CONTROLLER_AXIS_LEFTX = 0;
        private const int SDL_CONTROLLER_AXIS_LEFTY = 1;
        private const int SDL_CONTROLLER_AXIS_RIGHTX = 2;
        private const int SDL_CONTROLLER_AXIS_RIGHTY = 3;
        private const int SDL_CONTROLLER_AXIS_TRIGGERLEFT = 4;
        private const int SDL_CONTROLLER_AXIS_TRIGGERRIGHT = 5;

        private const int SDL_CONTROLLER_BUTTON_A = 0;
        private const int SDL_CONTROLLER_BUTTON_B = 1;
        private const int SDL_CONTROLLER_BUTTON_X = 2;
        private const int SDL_CONTROLLER_BUTTON_Y = 3;
        private const int SDL_CONTROLLER_BUTTON_BACK = 4;
        private const int SDL_CONTROLLER_BUTTON_GUIDE = 5;
        private const int SDL_CONTROLLER_BUTTON_START = 6;
        private const int SDL_CONTROLLER_BUTTON_LEFTSTICK = 7;
        private const int SDL_CONTROLLER_BUTTON_RIGHTSTICK = 8;
        private const int SDL_CONTROLLER_BUTTON_LEFTSHOULDER = 9;
        private const int SDL_CONTROLLER_BUTTON_RIGHTSHOULDER = 10;
        private const int SDL_CONTROLLER_BUTTON_DPAD_UP = 11;
        private const int SDL_CONTROLLER_BUTTON_DPAD_DOWN = 12;
        private const int SDL_CONTROLLER_BUTTON_DPAD_LEFT = 13;
        private const int SDL_CONTROLLER_BUTTON_DPAD_RIGHT = 14;

        private const int GuideComboGraceMs = 180;
        private const int ShortcutChordGraceMs = 400;
        private const int VirtualKeyboardHoldDurationMs = 600;
        private const int VirtualKeyboardShortcutCooldownMs = 700;
        private const int GamepadMouseHoldDurationMs = 600;
        private const int GamepadMouseShortcutCooldownMs = 800;
        private const int LeftStickSoloClickMaxDurationMs = 500;
        private const int AnalogPollIntervalMs = 16;

        private readonly AnikiHelperSettings settings;
        private readonly ILogger logger;
        private readonly Action onShortcutPressed;
        private readonly Action onVirtualKeyboardShortcutPressed;
        private readonly Action onGamepadMouseToggle;
        private readonly Func<bool> isGamepadMouseActive;
        private readonly Action<GamepadMouseInputState> onGamepadMouseInput;
        private readonly Action onGamepadMouseSuspendInput;
        private readonly Func<bool> isOverlayEnabled;
        private readonly Func<bool> isOverlayVisible;
        private readonly Action<ControllerInput> onOverlayButtonPressed;
        private readonly Func<bool> shouldUseSdlDigitalFallback;
        private readonly Func<bool> isWebBrowserActive;
        private readonly Action<WebBrowserGamepadInputState> onWebBrowserInput;

        // P10 provides the physical L3/R3 button state directly. Keep the existing event name
        // so Video Center does not need to know where the button state comes from.
        internal static event Action LeftStickClicked;

        private readonly Dictionary<int, HashSet<ControllerInput>> heldButtonsByController =
            new Dictionary<int, HashSet<ControllerInput>>();
        private readonly HashSet<int> analogControllerIds = new HashSet<int>();
        private readonly HashSet<ControllerInput> sdlFallbackHeldButtons = new HashSet<ControllerInput>();

        private DispatcherTimer analogTimer;
        private bool isStarted;
        private bool analogBridgeAvailable = true;
        private bool analogBridgeSuccessLogged;
        private bool sdlDigitalFallbackActive;
        private bool sdlDigitalFallbackSuccessLogged;
        private bool sdlDigitalFallbackNoControllerLogged;

        private bool shortcutHeld;
        private bool virtualKeyboardShortcutHeld;
        private bool gamepadMouseShortcutHeld;
        private bool browserBackPressPending;
        private bool browserBackChordConsumed;
        private bool browserShortcutSuppressionActive;
        private bool leftStickSoloClickCandidate;

        private DateTime leftStickSoloPressedAt = DateTime.MinValue;
        private DateTime? guidePressedAt;
        private DateTime? virtualKeyboardHoldStartedAt;
        private DateTime? gamepadMouseHoldStartedAt;
        private DateTime lastShortcutTime = DateTime.MinValue;
        private DateTime lastStartPressedTime = DateTime.MinValue;
        private DateTime lastBackPressedTime = DateTime.MinValue;
        private DateTime lastYPressedTime = DateTime.MinValue;
        private DateTime lastVirtualKeyboardShortcutTime = DateTime.MinValue;
        private DateTime lastGamepadMouseShortcutTime = DateTime.MinValue;

        private struct ButtonTransition
        {
            public bool PressedNow;
            public bool ReleasedNow;
        }

        private struct AnalogState
        {
            public short LeftX;
            public short LeftY;
            public short RightX;
            public short RightY;
            public short LeftTrigger;
            public short RightTrigger;
        }

        public AnikiOverlayInputListener(
            AnikiHelperSettings settings,
            ILogger logger,
            Action onShortcutPressed,
            Action onVirtualKeyboardShortcutPressed,
            Action onGamepadMouseToggle,
            Func<bool> isGamepadMouseActive,
            Action<GamepadMouseInputState> onGamepadMouseInput,
            Action onGamepadMouseSuspendInput,
            Func<bool> isOverlayEnabled,
            Func<bool> isOverlayVisible,
            Action<ControllerInput> onOverlayButtonPressed,
            Func<bool> shouldUseSdlDigitalFallback,
            Func<bool> isWebBrowserActive,
            Action<WebBrowserGamepadInputState> onWebBrowserInput)
        {
            this.settings = settings;
            this.logger = logger;
            this.onShortcutPressed = onShortcutPressed;
            this.onVirtualKeyboardShortcutPressed = onVirtualKeyboardShortcutPressed;
            this.onGamepadMouseToggle = onGamepadMouseToggle;
            this.isGamepadMouseActive = isGamepadMouseActive;
            this.onGamepadMouseInput = onGamepadMouseInput;
            this.onGamepadMouseSuspendInput = onGamepadMouseSuspendInput;
            this.isOverlayEnabled = isOverlayEnabled;
            this.isOverlayVisible = isOverlayVisible;
            this.onOverlayButtonPressed = onOverlayButtonPressed;
            this.shouldUseSdlDigitalFallback = shouldUseSdlDigitalFallback;
            this.isWebBrowserActive = isWebBrowserActive;
            this.onWebBrowserInput = onWebBrowserInput;
        }

        public void Start()
        {
            if (isStarted)
            {
                return;
            }

            isStarted = true;
            StartAnalogTimer();

            DebugLog(
                $"[AnikiHelper][OverlayInput][P10] Native button routing started. " +
                $"ControllersSeen={analogControllerIds.Count}. " +
                "SDL borrows Playnite-owned handles for analog input and in-game digital fallback only; " +
                "Aniki does not init/open/update/close SDL controllers.");
        }

        public void Stop()
        {
            if (!isStarted)
            {
                return;
            }

            isStarted = false;
            StopAnalogTimer();
            onGamepadMouseSuspendInput?.Invoke();

            heldButtonsByController.Clear();
            analogControllerIds.Clear();
            sdlFallbackHeldButtons.Clear();
            sdlDigitalFallbackActive = false;
            ResetTransientState();

            DebugLog("[AnikiHelper][OverlayInput][P10] Native controller router stopped. No SDL worker thread to join.");
        }

        public void HandleControllerConnected(OnControllerConnectedArgs args)
        {
            try
            {
                var controller = args?.Controller;
                if (controller == null)
                {
                    return;
                }

                // Normally we seed borrowed SDL access only after Playnite reports native input,
                // which preserves Playnite's controller filtering. If a controller is connected
                // while the game already owns foreground, P10 digital callbacks may be suspended;
                // in that specific case the connection InstanceId is enough to enable the fallback.
                var fallbackNeeded = false;
                try { fallbackNeeded = shouldUseSdlDigitalFallback?.Invoke() == true; } catch { }

                if (fallbackNeeded)
                {
                    analogControllerIds.Add(controller.InstanceId);
                }

                DebugLog(
                    $"[AnikiHelper][OverlayInput][P10] Controller connected. " +
                    $"InstanceId={controller.InstanceId}, Name='{controller.Name}', FallbackSeeded={fallbackNeeded}. " +
                    "Normal SDL access still follows Playnite-owned handles.");
            }
            catch (Exception ex)
            {
                DebugLog(ex, "[AnikiHelper][OverlayInput][P10] Failed to register connected controller.");
            }
        }

        public void HandleControllerDisconnected(OnControllerDisconnectedArgs args)
        {
            try
            {
                var controller = args?.Controller;
                if (controller == null)
                {
                    return;
                }

                analogControllerIds.Remove(controller.InstanceId);
                heldButtonsByController.Remove(controller.InstanceId);
                if (analogControllerIds.Count == 0)
                {
                    sdlFallbackHeldButtons.Clear();
                    sdlDigitalFallbackActive = false;
                }
                ResetTransientStateAfterTopologyChange();

                DebugLog(
                    $"[AnikiHelper][OverlayInput][P10] Controller disconnected. " +
                    $"InstanceId={controller.InstanceId}, Name='{controller.Name}'.");
            }
            catch (Exception ex)
            {
                DebugLog(ex, "[AnikiHelper][OverlayInput][P10] Failed to unregister disconnected controller.");
            }
        }

        /// <summary>Processes a P10 button-state update and reports whether Aniki consumed it.</summary>
        public bool HandleControllerButtonStateChanged(OnControllerButtonStateChangedArgs args)
        {
            if (!isStarted || args == null)
            {
                return false;
            }

            var transition = UpdateButtonState(args);
            return ProcessButtonTransition(args.Button, transition, "P10");
        }

        private bool ProcessButtonTransition(ControllerInput button, ButtonTransition transition, string source)
        {
            UpdatePressTimestamps(button, transition.PressedNow);
            UpdateLeftStickSoloCandidate(button, transition);

            // The overlay/Aniki keyboard must win over the Browser. During a browser keyboard
            // session the Browser stays visible, but B/Back belongs to the keyboard until it closes.
            if (isOverlayVisible?.Invoke() == true)
            {
                shortcutHeld = false;
                onGamepadMouseSuspendInput?.Invoke();

                if (transition.PressedNow)
                {
                    if (string.Equals(source, "SDL", StringComparison.Ordinal))
                    {
                        DebugLog($"[AnikiHelper][OverlayInput][SDL-Fallback] Overlay button pressed: {button}.");
                    }

                    RouteOverlayButton(button);
                }

                return true;
            }

            if (isWebBrowserActive?.Invoke() == true)
            {
                shortcutHeld = false;
                ResetVirtualKeyboardShortcutState();
                ResetGamepadMouseShortcutState();
                onGamepadMouseSuspendInput?.Invoke();

                RouteBrowserButton(button, transition);
                return true;
            }

            if (HandleBrowserPostCloseSuppression())
            {
                return false;
            }

            var gamepadMouseChordActive = ProcessGamepadMouseShortcut();
            var virtualKeyboardChordActive = ProcessVirtualKeyboardShortcut();

            if (gamepadMouseChordActive || virtualKeyboardChordActive)
            {
                onGamepadMouseSuspendInput?.Invoke();
                return false;
            }

            if (transition.ReleasedNow && button == ControllerInput.LeftStick)
            {
                TryRaiseLeftStickSoloClick();
            }

            if (ProcessOverlayShortcutEvent(button, transition))
            {
                return true;
            }

            return false;
        }

        private void StartAnalogTimer()
        {
            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null || dispatcher.HasShutdownStarted)
                {
                    logger?.Warn("[AnikiHelper] Analog controller bridge could not start because the WPF dispatcher is unavailable.");
                    return;
                }

                Action start = () =>
                {
                    if (!isStarted || analogTimer != null || dispatcher.HasShutdownStarted)
                    {
                        return;
                    }

                    analogTimer = new DispatcherTimer(DispatcherPriority.Input, dispatcher)
                    {
                        Interval = TimeSpan.FromMilliseconds(AnalogPollIntervalMs)
                    };
                    analogTimer.Tick += AnalogTimer_Tick;
                    analogTimer.Start();
                };

                if (dispatcher.CheckAccess())
                {
                    start();
                }
                else
                {
                    dispatcher.Invoke(start);
                }
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper] Failed to start analog controller bridge timer.");
            }
        }

        private void StopAnalogTimer()
        {
            var timer = analogTimer;
            analogTimer = null;

            if (timer == null)
            {
                return;
            }

            try
            {
                var dispatcher = timer.Dispatcher;
                Action stop = () =>
                {
                    try { timer.Stop(); } catch { }
                    try { timer.Tick -= AnalogTimer_Tick; } catch { }
                };

                if (dispatcher == null || dispatcher.HasShutdownStarted)
                {
                    return;
                }

                if (dispatcher.CheckAccess())
                {
                    stop();
                }
                else
                {
                    dispatcher.Invoke(stop);
                }
            }
            catch
            {
                // Dispatcher shutdown will tear down the timer. There is no background worker.
            }
        }

        private void AnalogTimer_Tick(object sender, EventArgs e)
        {
            if (!isStarted)
            {
                return;
            }

            try
            {
                var useSdlDigitalFallback = false;
                try { useSdlDigitalFallback = shouldUseSdlDigitalFallback?.Invoke() == true; } catch { }

                if (useSdlDigitalFallback)
                {
                    PollSdlDigitalFallback();
                }
                else if (sdlDigitalFallbackActive)
                {
                    StopSdlDigitalFallback();
                }

                // Hold-based shortcuts are driven by the currently active digital source:
                // P10 while Playnite owns input, or borrowed SDL button state while the game
                // owns foreground. The same state machine is reused for both paths.
                if (isOverlayVisible?.Invoke() != true && isWebBrowserActive?.Invoke() != true)
                {
                    if (HandleBrowserPostCloseSuppression())
                    {
                        return;
                    }

                    var mouseChord = ProcessGamepadMouseShortcut();
                    var keyboardChord = ProcessVirtualKeyboardShortcut();

                    if (mouseChord || keyboardChord)
                    {
                        onGamepadMouseSuspendInput?.Invoke();
                        return;
                    }

                    ProcessGuideShortcutGrace();
                }

                var overlayVisible = isOverlayVisible?.Invoke() == true;
                if (overlayVisible)
                {
                    onGamepadMouseSuspendInput?.Invoke();
                    return;
                }

                var browserActive = isWebBrowserActive?.Invoke() == true;
                var mouseActive = isGamepadMouseActive?.Invoke() == true;

                if (!browserActive && !mouseActive)
                {
                    return;
                }

                if (mouseActive && string.Equals(
                    settings?.InGameOverlayGamepadMouseShortcut,
                    "Disabled",
                    StringComparison.OrdinalIgnoreCase))
                {
                    onGamepadMouseToggle?.Invoke();
                    mouseActive = false;
                }

                if (!browserActive && !mouseActive)
                {
                    return;
                }

                var analog = ReadAnalogState();

                if (browserActive)
                {
                    onGamepadMouseSuspendInput?.Invoke();
                    onWebBrowserInput?.Invoke(new WebBrowserGamepadInputState
                    {
                        LeftX = analog.LeftX,
                        LeftY = analog.LeftY,
                        RightX = analog.RightX,
                        RightY = analog.RightY,
                        LeftClick = IsHeld(ControllerInput.A)
                    });
                    return;
                }

                if (mouseActive)
                {
                    onGamepadMouseInput?.Invoke(new GamepadMouseInputState
                    {
                        RightX = analog.RightX,
                        RightY = analog.RightY,
                        LeftY = analog.LeftY,
                        LeftTrigger = analog.LeftTrigger,
                        RightTrigger = analog.RightTrigger,
                        LeftClick = IsHeld(ControllerInput.A),
                        RightClick = IsHeld(ControllerInput.X)
                    });
                }
            }
            catch (Exception ex)
            {
                DebugLog(ex, "[AnikiHelper][OverlayInput][P10] Controller timer tick failed.");
            }
        }

        private void PollSdlDigitalFallback()
        {
            if (!analogBridgeAvailable)
            {
                return;
            }

            if (!sdlDigitalFallbackActive)
            {
                // P10 can stop between a press and its release when the game takes foreground.
                // Drop that stale native state before SDL becomes the temporary source of truth.
                heldButtonsByController.Clear();
                sdlFallbackHeldButtons.Clear();
                ResetTransientState();
                sdlDigitalFallbackActive = true;
                sdlDigitalFallbackNoControllerLogged = false;

                DebugLog(
                    "[AnikiHelper][OverlayInput][SDL-Fallback] Enabled because the current game owns controller foreground. " +
                    "Borrowing Playnite-owned SDL handles; no SDL init/open/update/close calls are made.");
            }

            if (analogControllerIds.Count == 0)
            {
                if (!sdlDigitalFallbackNoControllerLogged)
                {
                    sdlDigitalFallbackNoControllerLogged = true;
                    DebugLog(
                        "[AnikiHelper][OverlayInput][SDL-Fallback] Waiting for a Playnite-known controller InstanceId; " +
                        "use/connect the controller in Playnite once so its owned SDL handle can be borrowed.");
                }

                return;
            }

            var states = new Dictionary<ControllerInput, bool>
            {
                [ControllerInput.A] = false,
                [ControllerInput.B] = false,
                [ControllerInput.X] = false,
                [ControllerInput.Y] = false,
                [ControllerInput.Back] = false,
                [ControllerInput.Guide] = false,
                [ControllerInput.Start] = false,
                [ControllerInput.LeftStick] = false,
                [ControllerInput.RightStick] = false,
                [ControllerInput.LeftShoulder] = false,
                [ControllerInput.RightShoulder] = false,
                [ControllerInput.DPadUp] = false,
                [ControllerInput.DPadDown] = false,
                [ControllerInput.DPadLeft] = false,
                [ControllerInput.DPadRight] = false
            };

            var lockTaken = false;

            try
            {
                SDL_LockJoysticks();
                lockTaken = true;

                foreach (var instanceId in analogControllerIds.ToArray())
                {
                    var controller = SDL_GameControllerFromInstanceID(instanceId);
                    if (controller == IntPtr.Zero)
                    {
                        continue;
                    }

                    states[ControllerInput.A] |= IsSdlButtonPressed(controller, SDL_CONTROLLER_BUTTON_A);
                    states[ControllerInput.B] |= IsSdlButtonPressed(controller, SDL_CONTROLLER_BUTTON_B);
                    states[ControllerInput.X] |= IsSdlButtonPressed(controller, SDL_CONTROLLER_BUTTON_X);
                    states[ControllerInput.Y] |= IsSdlButtonPressed(controller, SDL_CONTROLLER_BUTTON_Y);
                    states[ControllerInput.Back] |= IsSdlButtonPressed(controller, SDL_CONTROLLER_BUTTON_BACK);
                    states[ControllerInput.Guide] |= IsSdlButtonPressed(controller, SDL_CONTROLLER_BUTTON_GUIDE);
                    states[ControllerInput.Start] |= IsSdlButtonPressed(controller, SDL_CONTROLLER_BUTTON_START);
                    states[ControllerInput.LeftStick] |= IsSdlButtonPressed(controller, SDL_CONTROLLER_BUTTON_LEFTSTICK);
                    states[ControllerInput.RightStick] |= IsSdlButtonPressed(controller, SDL_CONTROLLER_BUTTON_RIGHTSTICK);
                    states[ControllerInput.LeftShoulder] |= IsSdlButtonPressed(controller, SDL_CONTROLLER_BUTTON_LEFTSHOULDER);
                    states[ControllerInput.RightShoulder] |= IsSdlButtonPressed(controller, SDL_CONTROLLER_BUTTON_RIGHTSHOULDER);
                    states[ControllerInput.DPadUp] |= IsSdlButtonPressed(controller, SDL_CONTROLLER_BUTTON_DPAD_UP);
                    states[ControllerInput.DPadDown] |= IsSdlButtonPressed(controller, SDL_CONTROLLER_BUTTON_DPAD_DOWN);
                    states[ControllerInput.DPadLeft] |= IsSdlButtonPressed(controller, SDL_CONTROLLER_BUTTON_DPAD_LEFT);
                    states[ControllerInput.DPadRight] |= IsSdlButtonPressed(controller, SDL_CONTROLLER_BUTTON_DPAD_RIGHT);
                }
            }
            catch (DllNotFoundException ex)
            {
                DisableAnalogBridge(ex, "SDL2.dll is unavailable");
                return;
            }
            catch (EntryPointNotFoundException ex)
            {
                DisableAnalogBridge(ex, "required SDL button/locking entry point is unavailable");
                return;
            }
            catch (Exception ex)
            {
                DebugLog(ex, "[AnikiHelper][OverlayInput][SDL-Fallback] Failed to read borrowed SDL button state.");
                return;
            }
            finally
            {
                if (lockTaken)
                {
                    try { SDL_UnlockJoysticks(); } catch { }
                }
            }

            foreach (var state in states)
            {
                var transition = UpdateSdlFallbackButtonState(state.Key, state.Value);
                if (!transition.PressedNow && !transition.ReleasedNow)
                {
                    continue;
                }

                ProcessButtonTransition(state.Key, transition, "SDL");
            }

            if (!sdlDigitalFallbackSuccessLogged)
            {
                sdlDigitalFallbackSuccessLogged = true;
                DebugLog(
                    "[AnikiHelper][OverlayInput][SDL-Fallback] Digital state is available from Playnite-owned SDL handles.");
            }
        }

        private void StopSdlDigitalFallback()
        {
            sdlDigitalFallbackActive = false;
            sdlFallbackHeldButtons.Clear();

            // Do not clear heldButtonsByController here: P10 may already have resumed and
            // delivered a fresh button event before this timer observes the foreground change.
            // Keeping the native state avoids dropping that first valid P10 transition.
            ResetTransientState();
            DebugLog("[AnikiHelper][OverlayInput][SDL-Fallback] Disabled; native P10 button routing resumed.");
        }

        private ButtonTransition UpdateSdlFallbackButtonState(ControllerInput button, bool pressed)
        {
            var wasHeld = sdlFallbackHeldButtons.Contains(button);

            if (pressed)
            {
                sdlFallbackHeldButtons.Add(button);
            }
            else
            {
                sdlFallbackHeldButtons.Remove(button);
            }

            var isHeld = sdlFallbackHeldButtons.Contains(button);
            return new ButtonTransition
            {
                PressedNow = isHeld && !wasHeld,
                ReleasedNow = !isHeld && wasHeld
            };
        }

        private static bool IsSdlButtonPressed(IntPtr controller, int button)
        {
            return SDL_GameControllerGetButton(controller, button) != 0;
        }

        private AnalogState ReadAnalogState()
        {
            var result = new AnalogState();

            if (!analogBridgeAvailable || analogControllerIds.Count == 0)
            {
                return result;
            }

            var lockTaken = false;

            try
            {
                SDL_LockJoysticks();
                lockTaken = true;

                foreach (var instanceId in analogControllerIds.ToArray())
                {
                    var controller = SDL_GameControllerFromInstanceID(instanceId);
                    if (controller == IntPtr.Zero)
                    {
                        continue;
                    }

                    result.LeftX = SelectAxisWithGreatestMagnitude(
                        result.LeftX,
                        SDL_GameControllerGetAxis(controller, SDL_CONTROLLER_AXIS_LEFTX));
                    result.LeftY = SelectAxisWithGreatestMagnitude(
                        result.LeftY,
                        SDL_GameControllerGetAxis(controller, SDL_CONTROLLER_AXIS_LEFTY));
                    result.RightX = SelectAxisWithGreatestMagnitude(
                        result.RightX,
                        SDL_GameControllerGetAxis(controller, SDL_CONTROLLER_AXIS_RIGHTX));
                    result.RightY = SelectAxisWithGreatestMagnitude(
                        result.RightY,
                        SDL_GameControllerGetAxis(controller, SDL_CONTROLLER_AXIS_RIGHTY));
                    result.LeftTrigger = Math.Max(
                        result.LeftTrigger,
                        SDL_GameControllerGetAxis(controller, SDL_CONTROLLER_AXIS_TRIGGERLEFT));
                    result.RightTrigger = Math.Max(
                        result.RightTrigger,
                        SDL_GameControllerGetAxis(controller, SDL_CONTROLLER_AXIS_TRIGGERRIGHT));
                }

                if (!analogBridgeSuccessLogged)
                {
                    analogBridgeSuccessLogged = true;
                    DebugLog(
                        "[AnikiHelper][OverlayInput][Analog] Reading axes from Playnite-owned SDL controller handles. " +
                        "No SDL init/open/update/close calls are made by Aniki Helper.");
                }
            }
            catch (DllNotFoundException ex)
            {
                DisableAnalogBridge(ex, "SDL2.dll is unavailable");
            }
            catch (EntryPointNotFoundException ex)
            {
                DisableAnalogBridge(ex, "required SDL analog/locking entry point is unavailable");
            }
            catch (Exception ex)
            {
                DebugLog(ex, "[AnikiHelper][OverlayInput][Analog] Axis read failed.");
            }
            finally
            {
                if (lockTaken)
                {
                    try { SDL_UnlockJoysticks(); } catch { }
                }
            }

            return result;
        }

        private void DisableAnalogBridge(Exception ex, string reason)
        {
            if (!analogBridgeAvailable)
            {
                return;
            }

            analogBridgeAvailable = false;
            logger?.Warn(ex, $"[AnikiHelper] SDL controller bridge disabled because {reason}. Native P10 input remains available whenever Playnite forwards it.");
        }

        private ButtonTransition UpdateButtonState(OnControllerButtonStateChangedArgs args)
        {
            var controllerId = args.Controller?.InstanceId ?? int.MinValue;

            // Playnite only emits normal input state changes for controllers it is actively
            // processing. Registering the InstanceId here means the analog bridge follows the
            // same controller selection as P10, without trusting or duplicating SDL topology.
            if (args.Controller != null && analogControllerIds.Add(controllerId))
            {
                DebugLog(
                    $"[AnikiHelper][OverlayInput][P10] Analog controller registered from native input. " +
                    $"InstanceId={controllerId}, Name='{args.Controller.Name}'.");
            }

            var wasHeld = IsHeld(args.Button);

            if (!heldButtonsByController.TryGetValue(controllerId, out var buttons))
            {
                buttons = new HashSet<ControllerInput>();
                heldButtonsByController[controllerId] = buttons;
            }

            if (args.State == ControllerInputState.Pressed)
            {
                buttons.Add(args.Button);
            }
            else
            {
                buttons.Remove(args.Button);
                if (buttons.Count == 0)
                {
                    heldButtonsByController.Remove(controllerId);
                }
            }

            var isHeld = IsHeld(args.Button);
            return new ButtonTransition
            {
                PressedNow = isHeld && !wasHeld,
                ReleasedNow = !isHeld && wasHeld
            };
        }

        private bool IsHeld(ControllerInput button)
        {
            if (sdlFallbackHeldButtons.Contains(button))
            {
                return true;
            }

            foreach (var buttons in heldButtonsByController.Values)
            {
                if (buttons.Contains(button))
                {
                    return true;
                }
            }

            return false;
        }

        private void UpdatePressTimestamps(ControllerInput button, bool pressedNow)
        {
            if (!pressedNow)
            {
                return;
            }

            var now = DateTime.UtcNow;
            switch (button)
            {
                case ControllerInput.Start:
                    lastStartPressedTime = now;
                    break;
                case ControllerInput.Back:
                    lastBackPressedTime = now;
                    break;
                case ControllerInput.Y:
                    lastYPressedTime = now;
                    break;
            }
        }

        private void UpdateLeftStickSoloCandidate(ControllerInput button, ButtonTransition transition)
        {
            var now = DateTime.UtcNow;

            if (button == ControllerInput.LeftStick && transition.PressedNow)
            {
                leftStickSoloClickCandidate = true;
                leftStickSoloPressedAt = now;
            }

            // L3 is shared by L3+R3 keyboard and Start+L3 mouse mode. Any chord partner
            // cancels the solo-click candidate exactly like the previous SDL implementation.
            if (leftStickSoloClickCandidate &&
                (IsHeld(ControllerInput.RightStick) || IsHeld(ControllerInput.Start)))
            {
                leftStickSoloClickCandidate = false;
            }
        }

        private void TryRaiseLeftStickSoloClick()
        {
            var heldMs = leftStickSoloPressedAt == DateTime.MinValue
                ? double.MaxValue
                : (DateTime.UtcNow - leftStickSoloPressedAt).TotalMilliseconds;

            var raise = leftStickSoloClickCandidate &&
                        heldMs >= 0 &&
                        heldMs <= LeftStickSoloClickMaxDurationMs;

            leftStickSoloClickCandidate = false;
            leftStickSoloPressedAt = DateTime.MinValue;

            if (!raise)
            {
                return;
            }

            try
            {
                LeftStickClicked?.Invoke();
            }
            catch (Exception ex)
            {
                DebugLog(ex, "[AnikiHelper][OverlayInput][P10] L3 short-click listener failed.");
            }
        }

        private void RouteOverlayButton(ControllerInput button)
        {
            ControllerInput? routed = null;

            switch (button)
            {
                case ControllerInput.DPadLeft:
                case ControllerInput.DPadRight:
                case ControllerInput.DPadUp:
                case ControllerInput.DPadDown:
                case ControllerInput.A:
                case ControllerInput.X:
                case ControllerInput.Y:
                case ControllerInput.Start:
                    routed = button;
                    break;

                case ControllerInput.B:
                case ControllerInput.Back:
                    routed = ControllerInput.B;
                    break;
            }

            if (!routed.HasValue)
            {
                return;
            }

            DebugLog($"[AnikiHelper][OverlayInput] Overlay button pressed: {button} -> {routed.Value}.");
            onOverlayButtonPressed?.Invoke(routed.Value);
        }

        private void RouteBrowserButton(ControllerInput button, ButtonTransition transition)
        {
            var guide = IsHeld(ControllerInput.Guide);
            var start = IsHeld(ControllerInput.Start);
            var back = IsHeld(ControllerInput.Back);
            var y = IsHeld(ControllerInput.Y);
            var x = IsHeld(ControllerInput.X);
            var leftStick = IsHeld(ControllerInput.LeftStick);
            var rightStick = IsHeld(ControllerInput.RightStick);

            var browserShortcutChordHeld = IsBrowserShortcutChordHeld(
                guide,
                start,
                back,
                y,
                x,
                leftStick,
                rightStick);

            if (browserShortcutChordHeld)
            {
                browserShortcutSuppressionActive = true;
            }

            if (button == ControllerInput.Back && transition.PressedNow)
            {
                browserBackPressPending = true;
                browserBackChordConsumed = false;
            }

            if (browserBackPressPending && back &&
                (guide || start || y || x || leftStick || rightStick))
            {
                browserBackChordConsumed = true;
                browserShortcutSuppressionActive = true;
            }

            var closePressedNow = button == ControllerInput.Back &&
                                  transition.ReleasedNow &&
                                  browserBackPressPending &&
                                  !browserBackChordConsumed;

            if (button == ControllerInput.Back && transition.ReleasedNow)
            {
                browserBackPressPending = false;
                browserBackChordConsumed = false;
            }

            var suppressKeyboardButton = x && (back || guide);
            var suppressAddressButton = y && (back || guide);
            var suppressEnterButton = start && (back || leftStick);

            // Ignore threshold stick events here; browser analog input comes from the axis bridge.
            var shouldDispatchToBrowser =
                button == ControllerInput.A ||
                closePressedNow ||
                (transition.PressedNow &&
                 (button == ControllerInput.B ||
                  button == ControllerInput.X ||
                  button == ControllerInput.Y ||
                  button == ControllerInput.Start ||
                  button == ControllerInput.LeftShoulder ||
                  button == ControllerInput.RightShoulder ||
                  button == ControllerInput.DPadUp ||
                  button == ControllerInput.DPadDown ||
                  button == ControllerInput.DPadLeft ||
                  button == ControllerInput.DPadRight));

            if (!shouldDispatchToBrowser)
            {
                return;
            }

            onWebBrowserInput?.Invoke(new WebBrowserGamepadInputState
            {
                LeftClick = IsHeld(ControllerInput.A),
                ActivatePressed = button == ControllerInput.A && transition.PressedNow,
                BackPressed = button == ControllerInput.B && transition.PressedNow,
                ClosePressed = closePressedNow,
                KeyboardPressed = button == ControllerInput.X && transition.PressedNow && !suppressKeyboardButton,
                AddressPressed = button == ControllerInput.Y && transition.PressedNow && !suppressAddressButton,
                EnterPressed = button == ControllerInput.Start && transition.PressedNow && !suppressEnterButton,
                PreviousPressed = button == ControllerInput.LeftShoulder && transition.PressedNow,
                NextPressed = button == ControllerInput.RightShoulder && transition.PressedNow,
                DPadUpPressed = button == ControllerInput.DPadUp && transition.PressedNow,
                DPadDownPressed = button == ControllerInput.DPadDown && transition.PressedNow,
                DPadLeftPressed = button == ControllerInput.DPadLeft && transition.PressedNow,
                DPadRightPressed = button == ControllerInput.DPadRight && transition.PressedNow
            });
        }

        private bool HandleBrowserPostCloseSuppression()
        {
            if (!browserShortcutSuppressionActive)
            {
                browserBackPressPending = false;
                browserBackChordConsumed = false;
                return false;
            }

            if (IsAnyBrowserChordButtonHeld())
            {
                shortcutHeld = false;
                ResetVirtualKeyboardShortcutState();
                ResetGamepadMouseShortcutState();
                onGamepadMouseSuspendInput?.Invoke();
                return true;
            }

            ResetBrowserShortcutState();
            return false;
        }

        private bool IsAnyBrowserChordButtonHeld()
        {
            return IsHeld(ControllerInput.Guide) ||
                   IsHeld(ControllerInput.Start) ||
                   IsHeld(ControllerInput.Back) ||
                   IsHeld(ControllerInput.Y) ||
                   IsHeld(ControllerInput.X) ||
                   IsHeld(ControllerInput.LeftStick) ||
                   IsHeld(ControllerInput.RightStick);
        }

        private bool IsBrowserShortcutChordHeld(
            bool guide,
            bool start,
            bool back,
            bool y,
            bool x,
            bool leftStick,
            bool rightStick)
        {
            var mouseShortcut = settings?.InGameOverlayGamepadMouseShortcut ?? "BackR3";
            var keyboardShortcut = settings?.InGameOverlayVirtualKeyboardShortcut ?? "L3R3Hold";

            var mouseChordHeld =
                string.Equals(mouseShortcut, "StartL3", StringComparison.OrdinalIgnoreCase)
                    ? start && leftStick
                    : string.Equals(mouseShortcut, "GuideY", StringComparison.OrdinalIgnoreCase)
                        ? guide && y
                        : !string.Equals(mouseShortcut, "Disabled", StringComparison.OrdinalIgnoreCase) &&
                          back && rightStick;

            var keyboardChordHeld =
                string.Equals(keyboardShortcut, "BackX", StringComparison.OrdinalIgnoreCase)
                    ? back && x
                    : string.Equals(keyboardShortcut, "GuideX", StringComparison.OrdinalIgnoreCase)
                        ? guide && x
                        : !string.Equals(keyboardShortcut, "Disabled", StringComparison.OrdinalIgnoreCase) &&
                          leftStick && rightStick;

            var overlayShortcut = settings?.InGameOverlayControllerShortcut ?? "StartBack";
            var overlayChordHeld =
                string.Equals(overlayShortcut, "BackY", StringComparison.OrdinalIgnoreCase)
                    ? back && y
                    : string.Equals(overlayShortcut, "StartBack", StringComparison.OrdinalIgnoreCase)
                        ? start && back
                        : string.Equals(overlayShortcut, "Guide", StringComparison.OrdinalIgnoreCase) && guide;

            return mouseChordHeld || keyboardChordHeld || overlayChordHeld;
        }

        private bool ProcessGamepadMouseShortcut()
        {
            var shortcut = settings?.InGameOverlayGamepadMouseShortcut ?? "BackR3";

            if (string.Equals(shortcut, "Disabled", StringComparison.OrdinalIgnoreCase))
            {
                ResetGamepadMouseShortcutState();
                return false;
            }

            bool combinationHeld;
            switch (shortcut)
            {
                case "StartL3":
                    combinationHeld = IsHeld(ControllerInput.Start) && IsHeld(ControllerInput.LeftStick);
                    break;
                case "GuideY":
                    combinationHeld = IsHeld(ControllerInput.Guide) && IsHeld(ControllerInput.Y);
                    break;
                case "BackR3":
                default:
                    combinationHeld = IsHeld(ControllerInput.Back) && IsHeld(ControllerInput.RightStick);
                    break;
            }

            if (!combinationHeld)
            {
                ResetGamepadMouseShortcutState();
                return false;
            }

            if (string.Equals(shortcut, "GuideY", StringComparison.OrdinalIgnoreCase))
            {
                guidePressedAt = null;
            }

            if (gamepadMouseShortcutHeld)
            {
                return true;
            }

            if (!gamepadMouseHoldStartedAt.HasValue)
            {
                gamepadMouseHoldStartedAt = DateTime.UtcNow;
                return true;
            }

            if ((DateTime.UtcNow - gamepadMouseHoldStartedAt.Value).TotalMilliseconds >=
                GamepadMouseHoldDurationMs)
            {
                gamepadMouseShortcutHeld = true;
                TriggerGamepadMouseToggle();
            }

            return true;
        }

        private void ResetGamepadMouseShortcutState()
        {
            gamepadMouseShortcutHeld = false;
            gamepadMouseHoldStartedAt = null;
        }

        private void TriggerGamepadMouseToggle()
        {
            var now = DateTime.UtcNow;
            if ((now - lastGamepadMouseShortcutTime).TotalMilliseconds < GamepadMouseShortcutCooldownMs)
            {
                return;
            }

            lastGamepadMouseShortcutTime = now;

            try
            {
                DebugLog("[AnikiHelper][GamepadMouse][P10] Toggle shortcut detected.");
                onGamepadMouseToggle?.Invoke();
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper] P10 Gamepad Mouse shortcut callback failed.");
            }
        }

        private bool ProcessVirtualKeyboardShortcut()
        {
            if (isOverlayEnabled != null && !isOverlayEnabled())
            {
                ResetVirtualKeyboardShortcutState();
                return false;
            }

            var shortcut = settings?.InGameOverlayVirtualKeyboardShortcut ?? "L3R3Hold";
            if (string.Equals(shortcut, "Disabled", StringComparison.OrdinalIgnoreCase))
            {
                ResetVirtualKeyboardShortcutState();
                return false;
            }

            bool combinationHeld;
            bool requiresHold;

            switch (shortcut)
            {
                case "BackX":
                    combinationHeld = IsHeld(ControllerInput.Back) && IsHeld(ControllerInput.X);
                    requiresHold = false;
                    break;
                case "GuideX":
                    combinationHeld = IsHeld(ControllerInput.Guide) && IsHeld(ControllerInput.X);
                    requiresHold = false;
                    break;
                case "L3R3Hold":
                default:
                    combinationHeld = IsHeld(ControllerInput.LeftStick) && IsHeld(ControllerInput.RightStick);
                    requiresHold = true;
                    break;
            }

            if (!combinationHeld)
            {
                ResetVirtualKeyboardShortcutState();
                return false;
            }

            if (string.Equals(shortcut, "GuideX", StringComparison.OrdinalIgnoreCase))
            {
                guidePressedAt = null;
            }

            if (virtualKeyboardShortcutHeld)
            {
                return true;
            }

            if (!requiresHold)
            {
                virtualKeyboardShortcutHeld = true;
                TriggerVirtualKeyboardShortcut();
                return true;
            }

            if (!virtualKeyboardHoldStartedAt.HasValue)
            {
                virtualKeyboardHoldStartedAt = DateTime.UtcNow;
                return true;
            }

            if ((DateTime.UtcNow - virtualKeyboardHoldStartedAt.Value).TotalMilliseconds >=
                VirtualKeyboardHoldDurationMs)
            {
                virtualKeyboardShortcutHeld = true;
                TriggerVirtualKeyboardShortcut();
            }

            return true;
        }

        private void ResetVirtualKeyboardShortcutState()
        {
            virtualKeyboardShortcutHeld = false;
            virtualKeyboardHoldStartedAt = null;
        }

        private void TriggerVirtualKeyboardShortcut()
        {
            var now = DateTime.UtcNow;
            if ((now - lastVirtualKeyboardShortcutTime).TotalMilliseconds < VirtualKeyboardShortcutCooldownMs)
            {
                return;
            }

            lastVirtualKeyboardShortcutTime = now;

            try
            {
                DebugLog("[AnikiHelper][OverlayInput][P10] Virtual keyboard shortcut detected.");
                onVirtualKeyboardShortcutPressed?.Invoke();
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper] P10 virtual keyboard shortcut callback failed.");
            }
        }

        private bool ProcessOverlayShortcutEvent(ControllerInput button, ButtonTransition transition)
        {
            if (isOverlayEnabled != null && !isOverlayEnabled())
            {
                shortcutHeld = false;
                guidePressedAt = null;
                return false;
            }

            var shortcut = settings?.InGameOverlayControllerShortcut ?? "StartBack";
            if (string.Equals(shortcut, "Disabled", StringComparison.OrdinalIgnoreCase))
            {
                shortcutHeld = false;
                guidePressedAt = null;
                return false;
            }

            if (!IsSelectedOverlayChordHeld(shortcut))
            {
                shortcutHeld = false;
            }

            if (string.Equals(shortcut, "Guide", StringComparison.OrdinalIgnoreCase))
            {
                if (button != ControllerInput.Guide)
                {
                    return false;
                }

                if (transition.PressedNow)
                {
                    guidePressedAt = DateTime.UtcNow;

                    if (!IsGuideSharedWithAnotherShortcut())
                    {
                        guidePressedAt = null;
                        shortcutHeld = true;
                        TriggerShortcut("Guide press");
                        return true;
                    }

                    return false;
                }

                if (transition.ReleasedNow && guidePressedAt.HasValue)
                {
                    guidePressedAt = null;
                    shortcutHeld = true;
                    TriggerShortcut("Guide release");
                    return true;
                }

                return false;
            }

            if (!transition.PressedNow || shortcutHeld)
            {
                return false;
            }

            bool triggered;
            if (string.Equals(shortcut, "BackY", StringComparison.OrdinalIgnoreCase))
            {
                if (button != ControllerInput.Back && button != ControllerInput.Y)
                {
                    return false;
                }

                var directChord = IsHeld(ControllerInput.Back) && IsHeld(ControllerInput.Y);
                var graceChord = ArePressesWithinGrace(lastBackPressedTime, lastYPressedTime);
                triggered = directChord || graceChord;
            }
            else
            {
                if (button != ControllerInput.Start && button != ControllerInput.Back)
                {
                    return false;
                }

                var directChord = IsHeld(ControllerInput.Start) && IsHeld(ControllerInput.Back);
                var graceChord = ArePressesWithinGrace(lastStartPressedTime, lastBackPressedTime);
                triggered = directChord || graceChord;
            }

            if (!triggered)
            {
                return false;
            }

            shortcutHeld = true;
            TriggerShortcut(shortcut);
            return true;
        }

        private void ProcessGuideShortcutGrace()
        {
            if (!guidePressedAt.HasValue || shortcutHeld)
            {
                return;
            }

            if (isOverlayEnabled != null && !isOverlayEnabled())
            {
                guidePressedAt = null;
                return;
            }

            if (!string.Equals(
                settings?.InGameOverlayControllerShortcut,
                "Guide",
                StringComparison.OrdinalIgnoreCase))
            {
                guidePressedAt = null;
                return;
            }

            if (!IsHeld(ControllerInput.Guide))
            {
                return;
            }

            if (!IsGuideSharedWithAnotherShortcut())
            {
                return;
            }

            if ((DateTime.UtcNow - guidePressedAt.Value).TotalMilliseconds < GuideComboGraceMs)
            {
                return;
            }

            guidePressedAt = null;
            shortcutHeld = true;
            TriggerShortcut("Guide combo grace");
        }

        private bool IsGuideSharedWithAnotherShortcut()
        {
            return string.Equals(
                       settings?.InGameOverlayVirtualKeyboardShortcut,
                       "GuideX",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       settings?.InGameOverlayGamepadMouseShortcut,
                       "GuideY",
                       StringComparison.OrdinalIgnoreCase);
        }

        private bool IsSelectedOverlayChordHeld(string shortcut)
        {
            if (string.Equals(shortcut, "BackY", StringComparison.OrdinalIgnoreCase))
            {
                return IsHeld(ControllerInput.Back) && IsHeld(ControllerInput.Y);
            }

            if (string.Equals(shortcut, "Guide", StringComparison.OrdinalIgnoreCase))
            {
                return IsHeld(ControllerInput.Guide);
            }

            return IsHeld(ControllerInput.Start) && IsHeld(ControllerInput.Back);
        }

        private void TriggerShortcut(string source)
        {
            var now = DateTime.UtcNow;
            if ((now - lastShortcutTime).TotalMilliseconds < 500)
            {
                return;
            }

            lastShortcutTime = now;

            try
            {
                DebugLog($"[AnikiHelper][OverlayInput][P10] Overlay shortcut detected. Source={source}.");
                onShortcutPressed?.Invoke();
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper] P10 controller overlay shortcut callback failed.");
            }
        }

        private static bool ArePressesWithinGrace(DateTime firstPress, DateTime secondPress)
        {
            if (firstPress == DateTime.MinValue || secondPress == DateTime.MinValue)
            {
                return false;
            }

            return Math.Abs((firstPress - secondPress).TotalMilliseconds) <= ShortcutChordGraceMs;
        }

        private static short SelectAxisWithGreatestMagnitude(short current, short candidate)
        {
            return Math.Abs((int)candidate) > Math.Abs((int)current)
                ? candidate
                : current;
        }

        private void ResetBrowserShortcutState()
        {
            browserBackPressPending = false;
            browserBackChordConsumed = false;
            browserShortcutSuppressionActive = false;
        }

        private void ResetTransientStateAfterTopologyChange()
        {
            shortcutHeld = false;
            guidePressedAt = null;
            ResetVirtualKeyboardShortcutState();
            ResetGamepadMouseShortcutState();
            ResetBrowserShortcutState();
            leftStickSoloClickCandidate = false;
            leftStickSoloPressedAt = DateTime.MinValue;
            onGamepadMouseSuspendInput?.Invoke();
        }

        private void ResetTransientState()
        {
            ResetTransientStateAfterTopologyChange();
            lastStartPressedTime = DateTime.MinValue;
            lastBackPressedTime = DateTime.MinValue;
            lastYPressedTime = DateTime.MinValue;
        }

        private void DebugLog(string message)
        {
            try
            {
                if (global::AnikiHelper.AnikiHelper.Instance?.Settings?.EnableDebugLogs == true)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, message);
                }
            }
            catch
            {
                // Debug logging must never affect controller processing.
            }
        }

        private void DebugLog(Exception exception, string message)
        {
            try
            {
                if (global::AnikiHelper.AnikiHelper.Instance?.Settings?.EnableDebugLogs == true)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, exception, message);
                }
            }
            catch
            {
                // Debug logging must never affect controller processing.
            }
        }

        public void Dispose()
        {
            Stop();
        }

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GameControllerFromInstanceID(int joystickInstanceId);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern short SDL_GameControllerGetAxis(IntPtr gamecontroller, int axis);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern byte SDL_GameControllerGetButton(IntPtr gamecontroller, int button);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_LockJoysticks();

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SDL_UnlockJoysticks();
    }
}
