using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.GameTask.KeyMacro.Script;
using System;
using System.Collections.Generic;
using System.Threading;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.KeyMacro;

/// <summary>
/// 执行按键宏指令。按键原样发送，不经过游戏键位映射；记录本次执行按下的键，结束时全部松开。
/// 只在执行线程上使用，不是线程安全的
/// </summary>
internal sealed class KeyMacroExecutor
{
    /// <summary>
    /// 本次执行按下且未松开的键盘键
    /// </summary>
    private readonly HashSet<User32.VK> _pressedKeys = [];

    /// <summary>
    /// 本次执行按下且未松开的鼠标键
    /// </summary>
    private readonly HashSet<KeyMacroMouseButton> _pressedButtons = [];

    /// <summary>
    /// 执行一条指令。每条指令都重新取输入通道（InputHub 的约定）
    /// </summary>
    /// <param name="step">指令</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="shouldContinue">停止条件，等待期间按段检查</param>
    /// <returns>wait 被中断时返回 false，其余返回 true</returns>
    public bool Execute(KeyMacroStep step, CancellationToken ct, Func<bool> shouldContinue)
    {
        var input = InputHub.Foreground;
        switch (step.Kind)
        {
            case KeyMacroCommandKind.KeyDown:
                input.Keyboard.KeyDown(step.Key);
                _pressedKeys.Add(step.Key);
                return true;
            case KeyMacroCommandKind.KeyUp:
                input.Keyboard.KeyUp(step.Key);
                _pressedKeys.Remove(step.Key);
                return true;
            case KeyMacroCommandKind.KeyPress:
                input.Keyboard.KeyPress(step.Key);
                _pressedKeys.Remove(step.Key);
                return true;
            case KeyMacroCommandKind.MouseDown:
                ButtonDown(input.Mouse, step.Button);
                _pressedButtons.Add(step.Button);
                return true;
            case KeyMacroCommandKind.MouseUp:
                ButtonUp(input.Mouse, step.Button);
                _pressedButtons.Remove(step.Button);
                return true;
            case KeyMacroCommandKind.Click:
                ButtonClick(input.Mouse, step.Button);
                _pressedButtons.Remove(step.Button);
                return true;
            case KeyMacroCommandKind.MoveBy:
                input.Mouse.MoveMouseBy(step.X, step.Y);
                return true;
            case KeyMacroCommandKind.Scroll:
                input.Mouse.VerticalScroll(step.Amount);
                return true;
            case KeyMacroCommandKind.Wait:
                return PreciseDelay.Wait(step.WaitMs, ct, shouldContinue);
            default:
                return true;
        }
    }

    /// <summary>
    /// 松开本次执行按下的全部键。单个按键失败不影响其余按键
    /// </summary>
    /// <param name="onError">释放失败时的回调，用于记录日志</param>
    public void ReleaseAll(Action<Exception>? onError = null)
    {
        var input = InputHub.Foreground;
        foreach (var key in _pressedKeys)
        {
            try
            {
                input.Keyboard.KeyUp(key);
            }
            catch (Exception e)
            {
                onError?.Invoke(e);
            }
        }

        foreach (var button in _pressedButtons)
        {
            try
            {
                ButtonUp(input.Mouse, button);
            }
            catch (Exception e)
            {
                onError?.Invoke(e);
            }
        }

        _pressedKeys.Clear();
        _pressedButtons.Clear();
    }

    /// <summary>
    /// 按下鼠标键
    /// </summary>
    private static void ButtonDown(IMouseInput mouse, KeyMacroMouseButton button)
    {
        _ = button switch
        {
            KeyMacroMouseButton.Right => mouse.RightButtonDown(),
            KeyMacroMouseButton.Middle => mouse.MiddleButtonDown(),
            KeyMacroMouseButton.X1 => mouse.XButtonDown(1),
            KeyMacroMouseButton.X2 => mouse.XButtonDown(2),
            _ => mouse.LeftButtonDown()
        };
    }

    /// <summary>
    /// 松开鼠标键
    /// </summary>
    private static void ButtonUp(IMouseInput mouse, KeyMacroMouseButton button)
    {
        _ = button switch
        {
            KeyMacroMouseButton.Right => mouse.RightButtonUp(),
            KeyMacroMouseButton.Middle => mouse.MiddleButtonUp(),
            KeyMacroMouseButton.X1 => mouse.XButtonUp(1),
            KeyMacroMouseButton.X2 => mouse.XButtonUp(2),
            _ => mouse.LeftButtonUp()
        };
    }

    /// <summary>
    /// 点击鼠标键
    /// </summary>
    private static void ButtonClick(IMouseInput mouse, KeyMacroMouseButton button)
    {
        _ = button switch
        {
            KeyMacroMouseButton.Right => mouse.RightButtonClick(),
            KeyMacroMouseButton.Middle => mouse.MiddleButtonClick(),
            KeyMacroMouseButton.X1 => mouse.XButtonClick(1),
            KeyMacroMouseButton.X2 => mouse.XButtonClick(2),
            _ => mouse.LeftButtonClick()
        };
    }
}
