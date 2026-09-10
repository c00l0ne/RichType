using System;
using System.Diagnostics;
using System.Linq;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Kazrich.Core;

namespace Kazrich.App;

internal sealed record TextTarget(nint Window, int[] RuntimeId, string Suffix, Native.GuiInfo Caret)
{
    // UI Automation is called off the hook/UI thread. No clipboard and no full-field replacement.
    internal static TextTarget? Capture(nint window, string suffix, Settings settings, bool ownTest = false, bool checkBoundary = true)
    {
        try
        {
            if (Native.GetForegroundWindow() != window) return null;
            Native.GetWindowThreadProcessId(window, out var pid);
            using var process = Process.GetProcessById((int)pid);
            var isOwn = pid == Environment.ProcessId;
            if (!settings.AppSet().Contains(process.ProcessName) && !(ownTest && isOwn)) return null;
            var focused = AutomationElement.FocusedElement;
            if (focused == null || !EditableField(focused, pid, isOwn)) return null;
            if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out var raw)) return null;
            var pattern = (TextPattern)raw;
            var selections = pattern.GetSelection();
            if (selections.Length != 1) return null;
            var range = selections[0];
            if (range.CompareEndpoints(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End) != 0) return null;
            if (range.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is not bool readOnly || readOnly) return null;
            // Left/Right keystrokes do not follow logical text offsets in RTL
            // or vertical fields. Skip them before inference and recheck this
            // attribute before any replacement, including after a direction change.
            if (range.GetAttributeValue(TextPattern.TextFlowDirectionsAttribute) is FlowDirections flow && flow != FlowDirections.Default)
                return null;
            var document = pattern.DocumentRange;
            var preceding = range.Clone();
            var matchedSuffix = false;
            if (suffix.Length >= 256)
            {
                // Character-by-character range movement is very slow in some
                // providers. Search only before this exact caret and require
                // the found suffix to end at it; never accept an earlier match.
                try
                {
                    var beforeCaret = document.Clone();
                    beforeCaret.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.End);
                    var found = beforeCaret.FindText(suffix, true, false);
                    if (found == null && suffix.Contains('\n')) found = beforeCaret.FindText(suffix.Replace("\n", "\r\n"), true, false);
                    if (found != null && found.CompareEndpoints(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.End) == 0 &&
                        TypingQueue.Normalize(found.GetText(suffix.Length * 2 + 2)) == suffix)
                    { preceding = found; matchedSuffix = true; }
                }
                catch (Exception error) when (error is NotSupportedException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
            }
            preceding.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -(matchedSuffix ? 64 : suffix.Length + 64));
            if (preceding.CompareEndpoints(TextPatternRangeEndpoint.Start, document, TextPatternRangeEndpoint.Start) < 0)
                preceding.MoveEndpointByRange(TextPatternRangeEndpoint.Start, document, TextPatternRangeEndpoint.Start);
            var text = TypingQueue.Normalize(preceding.GetText(suffix.Length * 2 + 132));
            if (text.Length < suffix.Length || !MatchesInput(text.AsSpan(text.Length - suffix.Length), suffix,
                focused.Current.FrameworkId == "Chrome")) return null;
            var before = text.Length - suffix.Length;
            // A substring inside a larger token, address, path or username must never be corrected.
            if (checkBoundary && before > 0 && !(suffix.Length > 0 && char.IsWhiteSpace(suffix[0])) &&
                !char.IsWhiteSpace(text[before - 1]) && !"(\"«“[{".Contains(text[before - 1]) &&
                !TextBoundary.IsStart(text, before, settings.WordSet())) return null;
            // A provider can change focus, selection, text or editability while
            // serving an accessibility call, without generating a keyboard or
            // mouse event. Do not pair old text with the new native caret.
            var currentFocus = AutomationElement.FocusedElement;
            if (currentFocus == null || !EditableField(currentFocus, pid, isOwn) ||
                !focused.GetRuntimeId().SequenceEqual(currentFocus.GetRuntimeId())) return null;
            var currentSelections = pattern.GetSelection();
            if (currentSelections.Length != 1) return null;
            var currentRange = currentSelections[0];
            if (currentRange.CompareEndpoints(TextPatternRangeEndpoint.Start, currentRange, TextPatternRangeEndpoint.End) != 0 ||
                range.CompareEndpoints(TextPatternRangeEndpoint.Start, currentRange, TextPatternRangeEndpoint.Start) != 0 ||
                currentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is not bool stillReadOnly || stillReadOnly ||
                currentRange.GetAttributeValue(TextPattern.TextFlowDirectionsAttribute) is FlowDirections currentFlow && currentFlow != FlowDirections.Default)
                return null;
            if (TypingQueue.Normalize(preceding.GetText(suffix.Length * 2 + 132)) != text) return null;
            var gui = Native.GetGui(window);
            if (Native.GetForegroundWindow() != window || !focused.Current.HasKeyboardFocus) return null;
            return new(window, focused.GetRuntimeId(), suffix, gui);
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or
            System.Runtime.InteropServices.COMException or ArgumentException or System.ComponentModel.Win32Exception)
        { return null; }
    }

    private static bool MatchesInput(ReadOnlySpan<char> actual, ReadOnlySpan<char> expected, bool browserSpaces)
    {
        if (actual.Length != expected.Length) return false;
        for (var index = 0; index < actual.Length; index++)
        {
            // Chromium contenteditable uses NBSP to display typed trailing and
            // repeated spaces. Treat that representation as the tracked space;
            // the exact provider text is still re-read to detect later changes.
            if (actual[index] != expected[index] && !(browserSpaces && expected[index] == ' ' && actual[index] == '\u00a0'))
                return false;
        }
        return true;
    }

    private static bool EditableField(AutomationElement focused, uint processId, bool isOwn)
    {
        if (focused.Current.IsPassword || !focused.Current.IsEnabled || !focused.Current.HasKeyboardFocus ||
            focused.Current.ProcessId != processId || isOwn && focused.Current.AutomationId != "KazrichTypingArea") return false;
        // Browser search inputs with suggestions expose an editable combo box.
        if (focused.Current.ControlType != ControlType.Edit && focused.Current.ControlType != ControlType.Document &&
            focused.Current.ControlType != ControlType.ComboBox) return false;
        if (focused.Current.ControlType == ControlType.ComboBox &&
            (!focused.TryGetCurrentPattern(ValuePattern.Pattern, out var value) || ((ValuePattern)value).Current.IsReadOnly)) return false;
        // Some providers report password protection on an ancestor. Read-only
        // text is checked through the actual selected TextPattern range.
        var parent = focused;
        for (var index = 0; index < 4 && parent != null; index++)
        {
            if (parent.Current.IsPassword) return false;
            parent = TreeWalker.ControlViewWalker.GetParent(parent);
        }
        return true;
    }

    internal bool SameField(TextTarget other) => Window == other.Window && RuntimeId.SequenceEqual(other.RuntimeId);

    internal sealed record WordSnapshot(TextTarget Target, EditedWord Edit);
    internal static WordSnapshot? CaptureWord(nint window, Settings settings, bool preferRight)
    {
        try
        {
            // Reuse the same process, password, selection and read-only checks as queue capture.
            var target = Capture(window, "", settings, ownTest: true, checkBoundary: false);
            if (target == null) return null;
            var focused = AutomationElement.FocusedElement;
            var pattern = (TextPattern)focused.GetCurrentPattern(TextPattern.Pattern);
            var selections = pattern.GetSelection();
            if (selections.Length != 1) return null;
            var caret = selections[0];
            if (caret.CompareEndpoints(TextPatternRangeEndpoint.Start, caret, TextPatternRangeEndpoint.End) != 0) return null;
            var left = caret.Clone();
            left.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -64);
            var right = caret.Clone();
            right.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 64);
            // Chromium can move a range beyond an input into surrounding page
            // text and suggestions. Keep context inside the focused control.
            var document = pattern.DocumentRange;
            if (left.CompareEndpoints(TextPatternRangeEndpoint.Start, document, TextPatternRangeEndpoint.Start) < 0)
                left.MoveEndpointByRange(TextPatternRangeEndpoint.Start, document, TextPatternRangeEndpoint.Start);
            if (right.CompareEndpoints(TextPatternRangeEndpoint.End, document, TextPatternRangeEndpoint.End) > 0)
                right.MoveEndpointByRange(TextPatternRangeEndpoint.End, document, TextPatternRangeEndpoint.End);
            var leftText = TypingQueue.Normalize(left.GetText(132));
            var rightText = TypingQueue.Normalize(right.GetText(132));
            var edit = EditedWord.Around(leftText, rightText, preferRight);
            if (edit == null || !target.RuntimeId.SequenceEqual(focused.GetRuntimeId()) || Native.GetForegroundWindow() != window) return null;
            // Word context is read after the first field check. A provider can
            // change state during either read without a keyboard event, so
            // verify the field, caret and both sides before returning a snapshot.
            var verified = Capture(window, "", settings, ownTest: true, checkBoundary: false);
            if (verified == null || !target.SameField(verified) || !Native.SameCaret(target.Caret, verified.Caret) ||
                TypingQueue.Normalize(left.GetText(132)) != leftText || TypingQueue.Normalize(right.GetText(132)) != rightText ||
                Native.GetForegroundWindow() != window || !Native.SameCaret(verified.Caret, Native.GetGui(window))) return null;
            return new(verified, edit);
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or
            System.Runtime.InteropServices.COMException or ArgumentException)
        { return null; }
    }
}
