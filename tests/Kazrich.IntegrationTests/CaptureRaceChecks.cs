using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Kazrich.App;
using Kazrich.Core;

internal static class CaptureRaceChecks
{
    internal static void Run()
    {
        using var form = new Form { Text = "Kazrich isolated accessibility race checks", Size = new Size(700, 400), TopMost = true };
        var first = new InterceptedTextBox { AcceptsReturn = true, FontSize = 24 };
        var second = new System.Windows.Controls.TextBox { FontSize = 24 };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        layout.Controls.Add(new ElementHost { Child = first, Dock = DockStyle.Fill, Height = 120 }, 0, 0);
        layout.Controls.Add(new ElementHost { Child = second, Dock = DockStyle.Fill, Height = 120 }, 0, 1);
        form.Controls.Add(layout);
        AutomationProperties.SetAutomationId(first, "KazrichTypingArea");
        AutomationProperties.SetAutomationId(second, "KazrichTypingArea");
        var settings = new Settings { Backend = "OpenAI", Enabled = false, AllowedApps = "" };
        form.Shown += async (_, _) =>
        {
            try
            {
                foreach (var mode in new[] { "short", "long", "word-left", "word-right" })
                foreach (var scenario in new[] { "focus", "caret", "selection", "read-only", "disabled", "password", "role", "text", "boundary", "direction", "stable" })
                {
                    var longText = mode == "long";
                    var wordCapture = mode.StartsWith("word-", StringComparison.Ordinal);
                    first.AfterRead = null; first.IsReadOnly = false; first.IsEnabled = true; first.PasswordRole = false; first.ButtonRole = false;
                    first.FlowDirection = System.Windows.FlowDirection.LeftToRight;
                    var suffix = "првиет " + (longText ? new string('а', 300) : "");
                    first.Text = "abc " + suffix; first.CaretIndex = first.Text.Length; second.Text = "untouched other field";
                    form.Activate(); Native.SetForegroundWindow(form.Handle);
                    var testHandle = form.Handle;
                    await Task.Run(() => AutomationElement.FromHandle(testHandle).SetFocus());
                    first.Focus(); System.Windows.Input.Keyboard.Focus(first);
                    await Task.Delay(80);
                    if (Native.GetForegroundWindow() != form.Handle || !first.IsKeyboardFocused)
                        throw new InvalidOperationException($"The race fixture did not acquire its own field: foreground={Native.GetForegroundWindow() == form.Handle}, focus={first.IsKeyboardFocused}.");
                    var fired = false;
                    // CaptureWord first verifies the field through Capture (two
                    // reads), then reads left and right of the edited caret.
                    first.SkipReads = mode == "word-left" ? 2 : mode == "word-right" ? 3 : 0;
                    first.AfterRead = () =>
                    {
                        fired = true;
                        switch (scenario)
                        {
                            case "focus": second.Focus(); System.Windows.Input.Keyboard.Focus(second); break;
                            case "caret": first.CaretIndex = 0; break;
                            case "selection": first.Select(0, 3); break;
                            case "read-only": first.IsReadOnly = true; break;
                            case "disabled": first.IsEnabled = false; break;
                            case "password": first.PasswordRole = true; break;
                            case "role": first.ButtonRole = true; break;
                            case "text": first.Text = "protected changed content"; first.CaretIndex = first.Text.Length; break;
                            case "boundary": first.Text = "abc@" + suffix; first.CaretIndex = first.Text.Length; break;
                            case "direction": first.FlowDirection = System.Windows.FlowDirection.RightToLeft; break;
                        }
                    };
                    var accepted = await Task.Run(() => wordCapture
                        ? TextTarget.CaptureWord(form.Handle, settings, preferRight: false) != null
                        : TextTarget.Capture(form.Handle, suffix, settings, ownTest: true) != null);
                    var passed = fired && accepted == (scenario == "stable") && second.Text == "untouched other field";
                    Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: capture observes a provider-side change during text retrieval: {scenario}/{mode}; callback={fired}; accepted={accepted}");
                    if (!passed) Environment.ExitCode = 1;
                }
                first.AfterRead = null; first.IsReadOnly = false; first.IsEnabled = true; first.PasswordRole = false; first.ButtonRole = false;
                first.FlowDirection = System.Windows.FlowDirection.LeftToRight;
                first.Text = "првиет\u00a0"; first.CaretIndex = first.Text.Length;
                form.Activate(); Native.SetForegroundWindow(form.Handle);
                var spaceTestHandle = form.Handle;
                await Task.Run(() => AutomationElement.FromHandle(spaceTestHandle).SetFocus());
                first.Focus(); System.Windows.Input.Keyboard.Focus(first);
                await Task.Delay(80);
                foreach (var suffix in new[] { "првиет ", "првиет\u00a0" })
                {
                    var capture = await Task.Run(() => TextTarget.Capture(form.Handle, suffix, settings, ownTest: true));
                    var passed = (capture != null) == suffix.EndsWith('\u00a0') && first.Text == "првиет\u00a0";
                    Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: native WPF capture preserves exact nonbreaking spaces: expected NBSP={suffix.EndsWith('\u00a0')}");
                    if (!passed) Environment.ExitCode = 1;
                }
            }
            catch (Exception error) { Console.WriteLine(error); Environment.ExitCode = 1; }
            finally { form.Close(); }
        };
        Application.Run(form);
    }

    private sealed class InterceptedTextBox : System.Windows.Controls.TextBox
    {
        internal Action? AfterRead;
        internal int SkipReads;
        internal bool PasswordRole, ButtonRole;
        protected override AutomationPeer OnCreateAutomationPeer() => new InterceptedPeer(this);
        internal void ReadCompleted()
        {
            if (AfterRead != null && SkipReads > 0) { SkipReads--; return; }
            var callback = AfterRead; AfterRead = null;
            if (callback != null) { if (Dispatcher.CheckAccess()) callback(); else Dispatcher.Invoke(callback); }
        }
    }
    private sealed class InterceptedPeer(InterceptedTextBox owner) : TextBoxAutomationPeer(owner)
    {
        protected override bool IsPasswordCore() => owner.PasswordRole;
        protected override AutomationControlType GetAutomationControlTypeCore() => owner.ButtonRole ? AutomationControlType.Button : base.GetAutomationControlTypeCore();
        public override object GetPattern(PatternInterface patternInterface)
        {
            var pattern = base.GetPattern(patternInterface);
            return patternInterface == PatternInterface.Text && pattern is ITextProvider text ? new TextProvider(text, owner.ReadCompleted) : pattern;
        }
    }
    private sealed class TextProvider(ITextProvider inner, Action afterRead) : ITextProvider
    {
        private ITextRangeProvider Wrap(ITextRangeProvider range) => new TextRange(range, afterRead);
        public ITextRangeProvider[] GetSelection() => inner.GetSelection().Select(Wrap).ToArray();
        public ITextRangeProvider[] GetVisibleRanges() => inner.GetVisibleRanges().Select(Wrap).ToArray();
        public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) => Wrap(inner.RangeFromChild(childElement));
        public ITextRangeProvider RangeFromPoint(System.Windows.Point screenLocation) => Wrap(inner.RangeFromPoint(screenLocation));
        public ITextRangeProvider DocumentRange => Wrap(inner.DocumentRange);
        public SupportedTextSelection SupportedTextSelection => inner.SupportedTextSelection;
    }
    private sealed class TextRange(ITextRangeProvider inner, Action afterRead) : ITextRangeProvider
    {
        private ITextRangeProvider Inner => inner;
        private static ITextRangeProvider Unwrap(ITextRangeProvider value) => value is TextRange wrapped ? wrapped.Inner : value;
        private ITextRangeProvider Wrap(ITextRangeProvider value) => value == null ? null! : new TextRange(value, afterRead);
        public ITextRangeProvider Clone() => Wrap(inner.Clone());
        public bool Compare(ITextRangeProvider range) => inner.Compare(Unwrap(range));
        public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint) => inner.CompareEndpoints(endpoint, Unwrap(targetRange), targetEndpoint);
        public void ExpandToEnclosingUnit(TextUnit unit) => inner.ExpandToEnclosingUnit(unit);
        public ITextRangeProvider FindAttribute(int attributeId, object value, bool backward) => Wrap(inner.FindAttribute(attributeId, value, backward));
        public ITextRangeProvider FindText(string text, bool backward, bool ignoreCase) => Wrap(inner.FindText(text, backward, ignoreCase));
        public object GetAttributeValue(int attributeId) => inner.GetAttributeValue(attributeId);
        public double[] GetBoundingRectangles() => inner.GetBoundingRectangles();
        public IRawElementProviderSimple GetEnclosingElement() => inner.GetEnclosingElement();
        public string GetText(int maxLength) { var text = inner.GetText(maxLength); afterRead(); return text; }
        public int Move(TextUnit unit, int count) => inner.Move(unit, count);
        public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint) => inner.MoveEndpointByRange(endpoint, Unwrap(targetRange), targetEndpoint);
        public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count) => inner.MoveEndpointByUnit(endpoint, unit, count);
        public void Select() => inner.Select();
        public void AddToSelection() => inner.AddToSelection();
        public void RemoveFromSelection() => inner.RemoveFromSelection();
        public void ScrollIntoView(bool alignToTop) => inner.ScrollIntoView(alignToTop);
        public IRawElementProviderSimple[] GetChildren() => inner.GetChildren();
    }
}
