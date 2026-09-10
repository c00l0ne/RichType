using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Kazrich.Core;

namespace Kazrich.App;

internal sealed class CorrectionController : IDisposable
{
    private readonly Control ui;
    private readonly InputObserver observer;
    private readonly TypingQueue tracker = new();
    private readonly System.Windows.Forms.Timer idleTimer = new();
    private bool idleReady;
    private bool layoutSwitchPaused;
    private long layoutSwitchReleasedAt;
    private string recentContext = "";
    private readonly HashSet<string> recentLayoutWords = new(StringComparer.Ordinal);
    private long inputSettleUntil;
    private TypingBatch? awaitingContext;
    private CancellationTokenSource? pending;
    private CancellationTokenSource? editProbe;
    private bool inspectEdit = true, deletionEdit, deletionOnly;
    private int typedSinceReset;
    private long revision;
    private long generation;
    private nint window;
    private ModelClient client;
    private readonly WindowsSpellingHints spellingHints;
    private bool disposed;
    private readonly bool acceptSyntheticTestInput;
    private UndoEntry? undo;
    private EditUndoEntry? editUndo;
    private sealed record EditUndoEntry(TextTarget.WordSnapshot Original, string Replacement, int CaretOffset, long Revision);
    private sealed record UndoEntry(TextTarget Target, string Original, string Replacement, string Tail, long Revision);
    internal Settings Settings { get; private set; }
    internal event Action<string>? Status;
    internal event Action<Exception>? Fault;
    internal event Action? PausedAfterInputFailure;
    internal int Corrections { get; private set; }

    internal CorrectionController(Control ui, Settings settings, bool acceptSyntheticTestInput = false)
    {
        this.ui = ui;
        this.acceptSyntheticTestInput = acceptSyntheticTestInput;
        Settings = settings;
        spellingHints = new WindowsSpellingHints();
        try
        {
            client = new ModelClient(settings, spellingHints: spellingHints);
            observer = new InputObserver();
        }
        catch
        {
            client?.Dispose();
            spellingHints.Dispose();
            idleTimer.Dispose();
            throw;
        }
        observer.KeyPressed += OnKey;
        observer.PointerChanged += Reset;
        idleTimer.Tick += (_, _) => OnIdle();
    }

    internal void Configure(Settings settings)
    {
        // Validate and construct first. A rejected change must retain the
        // previous settings, client and pending input state.
        var replacement = new ModelClient(settings, spellingHints: spellingHints);
        Reset();
        Settings = settings;
        var old = client;
        client = replacement;
        old.Dispose();
        Status?.Invoke(settings.Enabled ? "Включён • ожидаю слово" : "На паузе");
    }

    internal void Reset()
    {
        revision++;
        generation++;
        var wasChecking = pending is { IsCancellationRequested: false } || editProbe is { IsCancellationRequested: false };
        pending?.Cancel();
        editProbe?.Cancel();
        tracker.Reset();
        recentContext = "";
        recentLayoutWords.Clear();
        inputSettleUntil = 0;
        awaitingContext = null;
        idleTimer.Stop(); idleReady = false;
        layoutSwitchPaused = false;
        layoutSwitchReleasedAt = 0;
        undo = null;
        editUndo = null;
        inspectEdit = true;
        typedSinceReset = 0;
        deletionEdit = false;
        deletionOnly = false;
        if (wasChecking && !disposed) Status?.Invoke("Проверка отменена: изменилось поле или позиция ввода.");
    }

    private void OnKey(Native.KeyboardData key)
    {
        // The hook never waits for a model or an accessibility provider.
        if (disposed) return;
        if (key.Vk == 0x5A && Native.Down(0x11) && Native.Down(0x12)) return; // Undo hotkey.
        // Ctrl+Alt (and the other modifier-only layout chords) must retain the
        // queue while the editor updates its input language. Preserve undo too.
        if (key.Vk is 0x11 or 0x12 or 0xA2 or 0xA3 or 0xA4 or 0xA5)
        {
            if ((key.Flags & 0x10) != 0 && !acceptSyntheticTestInput) Reset();
            else if (Settings.Enabled) PauseForLayoutSwitch();
            return;
        }
        if (key.Vk is 0x10 or 0xA0 or 0xA1) return;
        // Win+Space changes the input language, not the text. The Windows key
        // arrives first, so suspend until its chord is known instead of losing
        // the queued prefix. Other Windows shortcuts still reset below.
        if (Settings.Enabled && ((key.Flags & 0x10) == 0 || acceptSyntheticTestInput) &&
            !Native.Down(0x11) && !Native.Down(0x12) &&
            (key.Vk is 0x5B or 0x5C || key.Vk == 0x20 && (Native.Down(0x5B) || Native.Down(0x5C))))
        {
            PauseForLayoutSwitch();
            return;
        }
        layoutSwitchPaused = false;
        layoutSwitchReleasedAt = 0;
        revision++;
        undo = null;
        editUndo = null;
        if (!Settings.Enabled) { Reset(); return; }
        var current = Native.GetForegroundWindow();
        if (current != window) { Reset(); window = current; }
        if (((key.Flags & 0x10) != 0 && !acceptSyntheticTestInput) || Native.Down(0x11) || Native.Down(0x12) || Native.Down(0x5B) || Native.Down(0x5C))
        { Reset(); return; }
        idleReady = false;
        idleTimer.Stop();
        idleTimer.Interval = Math.Max(350, Settings.DelayMs);
        idleTimer.Start();
        if (key.Vk is 8 or 0x2E)
        {
            generation++;
            pending?.Cancel();
            editProbe?.Cancel();
            deletionOnly = true;
            awaitingContext = null;
            var queuedBackspace = key.Vk == 8 && !inspectEdit && tracker.Text.Length > 0;
            if (key.Vk == 8) tracker.Backspace(); else tracker.Reset();
            if (queuedBackspace)
            {
                // Editing the known tail must not discard earlier, unfinished words.
                Schedule();
                return;
            }
            inspectEdit = true;
            deletionEdit = true;
            recentContext = "";
            recentLayoutWords.Clear();
            ScheduleEditProbe();
            return;
        }
        var c = key.Vk == 0x0D ? '\n' : acceptSyntheticTestInput && key.Vk == 0xE7 ? (char?)key.Scan : Native.Translate(key, current);
        if (c == null || char.IsControl(c.Value) && c != '\n' && c != '\r') { Reset(); return; }
        // An unknown deletion may have emptied a selected field. Retain all
        // subsequent input until capture can distinguish a fresh phrase from
        // a suffix added to an existing word; its boundary is still verified.
        if (deletionOnly && inspectEdit && tracker.Text.Length == 0) tracker.Reset();
        deletionOnly = false;
        if (!tracker.Append(c.Value))
        {
            generation++;
            pending?.Cancel();
            Status?.Invoke("Очередь сброшена: превышен лимит или введён неподдерживаемый символ.");
            return;
        }
        if (inspectEdit) { typedSinceReset++; ScheduleEditProbe(); }
        else Schedule();
    }

    private void PauseForLayoutSwitch()
    {
        if (layoutSwitchPaused) return;
        layoutSwitchPaused = true;
        layoutSwitchReleasedAt = 0;
        generation++;
        pending?.Cancel();
        editProbe?.Cancel();
        idleReady = false;
        idleTimer.Stop();
        idleTimer.Interval = 50;
        idleTimer.Start();
    }

    private void OnIdle()
    {
        var resumeEdit = layoutSwitchPaused && inspectEdit && typedSinceReset > 0;
        if (layoutSwitchPaused)
        {
            if (Native.ModifiersDown()) return;
            if (layoutSwitchReleasedAt == 0) layoutSwitchReleasedAt = Environment.TickCount64;
            // Let the language picker close, then use the normal strict text,
            // field and caret checks before applying any queued correction.
            if (Native.GetForegroundWindow() != window)
            {
                if (Environment.TickCount64 - layoutSwitchReleasedAt < 750) return;
                Reset();
                return;
            }
            layoutSwitchPaused = false;
            layoutSwitchReleasedAt = 0;
        }
        idleTimer.Stop();
        idleReady = true;
        if (resumeEdit) ScheduleEditProbe();
        else Schedule();
    }

    // Deletion leaves a deliberate fragment. Earlier completed words can still be checked.
    private TypingBatch? NextBatch()
    {
        var batch = tracker.Next(includeUnfinished: idleReady && !deletionOnly,
            hasContext: (Settings.FixKeyboardLayout || Settings.FixHyphens) && recentContext.Any(CorrectionRules.IsLetter));
        return batch == awaitingContext ? null : batch;
    }

    private void Schedule()
    {
        if (!disposed && !layoutSwitchPaused && !inspectEdit && Settings.Enabled && NextBatch() != null)
            ui.BeginInvoke((Action)Start);
    }

    private void ScheduleEditProbe()
    {
        editProbe?.Cancel();
        var cancellation = new CancellationTokenSource();
        editProbe = cancellation;
        var expectedRevision = revision;
        var epoch = generation;
        ui.BeginInvoke((Action)(() => ReviewEditedWord(cancellation, expectedRevision, epoch)));
    }

    private async void ReviewEditedWord(CancellationTokenSource cancellation, long expectedRevision, long epoch)
    {
        var token = cancellation.Token;
        try
        {
            await Task.Delay(30, token);
            var snapshot = await Task.Run(() => TextTarget.CaptureWord(window, Settings, deletionEdit), token);
            if (revision != expectedRevision || generation != epoch || token.IsCancellationRequested) return;
            // The hook can observe a whole burst before the editor exposes its
            // final caret. Verify the complete tracked suffix before deciding
            // whether this is fresh input or an edit inside an existing word.
            if (!deletionOnly && tracker.Next(includeUnfinished: true) != null)
            {
                var queued = await CaptureCurrent(window, epoch, token, waitForInput: true);
                if (revision != expectedRevision || generation != epoch || token.IsCancellationRequested) return;
                if (queued != null)
                {
                    snapshot = await Task.Run(() => TextTarget.CaptureWord(window, Settings, deletionEdit), token);
                    if (revision != expectedRevision || generation != epoch || token.IsCancellationRequested) return;
                    if (snapshot == null || snapshot.Edit.CaretOffset >= snapshot.Edit.Word.Length &&
                        !snapshot.Edit.After.Any(c => !char.IsWhiteSpace(c)))
                    {
                        inspectEdit = false;
                        deletionEdit = false;
                        Schedule();
                        return;
                    }
                }
            }
            if (deletionOnly)
            {
                Status?.Invoke("Слово сокращено • проверка после продолжения ввода");
                return;
            }
            var existingWord = snapshot != null && (deletionEdit || snapshot.Edit.Word.Length > typedSinceReset ||
                snapshot.Edit.CaretOffset < snapshot.Edit.Word.Length || snapshot.Edit.After.Any(c => !char.IsWhiteSpace(c)));
            if (!existingWord)
            {
                inspectEdit = false;
                Schedule();
                return;
            }
            await Task.Delay(Math.Max(200, Settings.DelayMs), token);
            if (revision != expectedRevision || generation != epoch) return;
            var currentClient = client;
            Status?.Invoke("Проверяю отредактированное слово…");
            var corrected = await currentClient.CorrectPhraseAsync(snapshot!.Edit.Word, token,
                snapshot.Edit.Before, snapshot.Edit.After);
            corrected = await currentClient.CorrectHyphensAsync(corrected, token);
            if (revision != expectedRevision || generation != epoch || token.IsCancellationRequested) return;
            if (corrected == snapshot.Edit.Word)
            {
                tracker.Reset(); recentContext = ""; recentLayoutWords.Clear(); awaitingContext = null;
                typedSinceReset = 0; deletionEdit = false;
                Status?.Invoke("Отредактированное слово проверено • без замены");
                return;
            }
            var verified = await Task.Run(() => TextTarget.CaptureWord(window, Settings, deletionEdit), token);
            if (revision != expectedRevision || generation != epoch || token.IsCancellationRequested) return;
            if (verified == null || !snapshot.Target.SameField(verified.Target) || snapshot.Edit != verified.Edit ||
                Native.ModifiersDown() || !Native.SameCaret(verified.Target.Caret, Native.GetGui(window)))
            {
                Status?.Invoke("Замена слова пропущена: текст или курсор изменились.");
                return;
            }
            var caretAfter = snapshot.Edit.CaretAfter(corrected);
            if (!Native.ReplaceWordAtCaret(snapshot.Edit.Word.Length, snapshot.Edit.CaretOffset, corrected, caretAfter))
            {
                Reset(); Settings = Settings with { Enabled = false }; PausedAfterInputFailure?.Invoke();
                Status?.Invoke("Windows не выполнила замену слова полностью. Проверьте поле.");
                return;
            }
            AllowInjectedInputToSettle(snapshot.Edit.Word.Length, corrected.Length);
            tracker.Reset(); recentContext = ""; recentLayoutWords.Clear(); awaitingContext = null; typedSinceReset = 0; deletionEdit = false;
            revision++;
            editUndo = new(snapshot, corrected, caretAfter, revision);
            Corrections++;
            Status?.Invoke("Отредактированное слово исправлено");
        }
        catch (OperationCanceledException)
        {
            if (!token.IsCancellationRequested && !disposed) Status?.Invoke("Модель не ответила вовремя при проверке слова.");
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
        {
            if (!disposed && !token.IsCancellationRequested) { Fault?.Invoke(e); Status?.Invoke("Не удалось проверить отредактированное слово."); }
        }
        finally
        {
            if (ReferenceEquals(editProbe, cancellation)) editProbe = null;
            cancellation.Dispose();
        }
    }

    private sealed record Snapshot(TextTarget Target, string Text, long Revision);
    private async Task<Snapshot?> CaptureCurrent(nint targetWindow, long epoch, CancellationToken token, bool waitForInput = false)
    {
        // A large burst can reach the keyboard hook long before an editor has
        // exposed all of it through accessibility. Give that known backlog the
        // same bounded catch-up opportunity as a long correction of our own.
        var settleDeadline = Math.Max(Environment.TickCount64 + Math.Clamp(tracker.Text.Length * 20, 750, 5000), inputSettleUntil);
        while (!disposed && generation == epoch)
        {
            token.ThrowIfCancellationRequested();
            var expectedRevision = revision;
            var text = tracker.Text;
            if (text.Length == 0) return null;
            var expectedSuffix = recentContext + text;
            var target = await Task.Run(() => TextTarget.Capture(targetWindow, expectedSuffix, Settings, ownTest: true), token);
            token.ThrowIfCancellationRequested();
            if (generation != epoch) return null;
            if (revision != expectedRevision)
            {
                await Task.Delay(20, token); // Retry the position check, not the model request.
                continue;
            }
            if (target == null && waitForInput && Environment.TickCount64 < settleDeadline &&
                Native.GetForegroundWindow() == targetWindow)
            {
                await Task.Delay(25, token);
                continue;
            }
            return target == null ? null : new Snapshot(target, text, expectedRevision);
        }
        return null;
    }

    private async void Start()
    {
        if (disposed || layoutSwitchPaused || inspectEdit || pending != null || !Settings.Enabled || NextBatch() == null) return;
        using var cts = new CancellationTokenSource();
        pending = cts;
        var currentClient = client;
        var epoch = generation;
        var targetWindow = window;
        var allowRestart = true;
        try
        {
            // Coalesce the initial burst once; subsequent keys do not restart this timer or inference.
            if (!idleReady) await Task.Delay(Math.Max(30, Settings.DelayMs), cts.Token);
            while (!disposed && generation == epoch && NextBatch() is { } batch)
            {
                cts.Token.ThrowIfCancellationRequested();
                // The hook sees keystrokes before the editor and its accessibility
                // provider apply them. Give the initial capture time to catch up.
                var target = await CaptureCurrent(targetWindow, epoch, cts.Token, waitForInput: true);
                if (target == null)
                {
                    Reset();
                    Status?.Invoke("Проверка пропущена: поле или позиция курсора не подтверждены.");
                    return;
                }
                Status?.Invoke($"Проверяю очередь • {tracker.Text.Length} символов");
                var watch = Stopwatch.StartNew();
                // The next word may already be visible while only the completed
                // prefix is being replaced. Supply it as read-only context, and
                // keep Enter as a hard sentence boundary.
                var followingContext = batch.Consumed == batch.Text.Length ? tracker.Text[batch.Consumed..] : "";
                var corrected = string.IsNullOrWhiteSpace(batch.Text) ? batch.Text :
                    await currentClient.CorrectPhraseAsync(batch.Text, cts.Token, recentContext, followingContext);
                var replacementPrefix = Settings.FixHyphens ? Hyphenation.ContextSuffix(recentContext) : "";
                var replacement = await currentClient.CorrectHyphensAsync(replacementPrefix + corrected, cts.Token);
                if (replacement == replacementPrefix + corrected)
                {
                    replacementPrefix = "";
                    replacement = corrected;
                }
                if (replacementPrefix.Length == 0 && Settings.FixKeyboardLayout)
                {
                    var spellingPrefix = ModelClient.SpellingContextSuffix(recentContext);
                    var reviewedPrefix = await currentClient.CorrectPrecedingSpellingAsync(spellingPrefix, corrected, cts.Token,
                        recentLayoutWords.ToHashSet(StringComparer.Ordinal));
                    if (reviewedPrefix != spellingPrefix)
                    {
                        replacementPrefix = spellingPrefix;
                        replacement = reviewedPrefix + replacement;
                    }
                }
                var replacementOriginal = replacementPrefix + batch.Text;
                var changed = replacement != replacementOriginal;
                cts.Token.ThrowIfCancellationRequested();
                if (generation != epoch) return;
                if (!tracker.Matches(batch)) continue; // A punctuation key turned out to be inside a word.
                while (Native.ModifiersDown()) await Task.Delay(25, cts.Token);
                // Newly typed keys can still be waiting in the editor while the
                // model finishes. Retry the same strict snapshot before rejecting it.
                var verified = await CaptureCurrent(targetWindow, epoch, cts.Token, waitForInput: true);
                if (verified == null || !target.Target.SameField(verified.Target) ||
                    Native.GetForegroundWindow() != targetWindow || !Native.SameCaret(verified.Target.Caret, Native.GetGui(targetWindow)))
                {
                    Reset();
                    Status?.Invoke("Замена пропущена: изменилось поле, текст или позиция курсора.");
                    return;
                }
                // Input can also resume while accessibility is verifying the caret.
                if (!tracker.Matches(batch)) continue;
                if (changed && Settings.FixKeyboardLayout)
                {
                    var prefixBatch = batch.DeferUnchangedShortWord(replacement);
                    if (prefixBatch != batch)
                    {
                        // Verify the original whole batch before retaining its unresolved
                        // ending. The retained word stays in the tail and gets new context.
                        replacement = replacement[..(replacement.Length - batch.Text.Length + prefixBatch.Text.Length)];
                        batch = prefixBatch;
                        replacementOriginal = replacementPrefix + batch.Text;
                    }
                }
                var tail = verified.Text[batch.Text.Length..];
                if (Native.ModifiersDown()) continue;
                if (changed)
                {
                    if (!Native.ReplaceBeforeTail(replacementOriginal.Length, replacement, tail))
                    {
                        Reset();
                        Settings = Settings with { Enabled = false };
                        PausedAfterInputFailure?.Invoke();
                        Status?.Invoke("Windows не выполнила замену полностью. Проверьте поле; корректор поставлен на паузу.");
                        return;
                    }
                    AllowInjectedInputToSettle(replacementOriginal.Length, replacement.Length, 2 * tail.Length);
                    revision++;
                    undo = new(verified.Target, replacementOriginal, replacement, tail, revision);
                    Corrections++;
                }
                var isolated = batch.Text.Trim();
                if (!changed && Settings.FixKeyboardLayout && batch.Consumed == batch.Text.Length &&
                    !batch.Text.Contains('\n') && isolated.Length >= 1 && isolated.All(CorrectionRules.IsLetter))
                {
                    // An unchanged isolated word may need a neighbour to disambiguate its layout.
                    // Keep it verified in the queue, but do not ask again until the batch changes.
                    awaitingContext = batch;
                    Status?.Invoke("Проверено • ожидаю соседнее слово для уточнения раскладки");
                    return;
                }
                awaitingContext = null;
                tracker.Consume(batch);
                // A new literal occurrence must not inherit an earlier word's conversion.
                recentLayoutWords.ExceptWith(System.Text.RegularExpressions.Regex.Split(replacement, @"\s+"));
                recentLayoutWords.UnionWith(KeyboardLayout.ConvertedWords(replacementOriginal, replacement, Settings.WordSet()));
                recentContext = recentContext[..(recentContext.Length - replacementPrefix.Length)] + replacement +
                    (batch.Consumed > batch.Text.Length ? "\n" : "");
                recentContext = recentContext[(recentContext.LastIndexOf('\n') + 1)..];
                if (recentContext.Length > 160)
                {
                    var cut = recentContext.Length - 160;
                    while (cut < recentContext.Length && !char.IsWhiteSpace(recentContext[cut - 1])) cut++;
                    recentContext = recentContext[cut..];
                }
                recentLayoutWords.IntersectWith(System.Text.RegularExpressions.Regex.Split(recentContext, @"\s+"));
                if (batch.Unfinished && tracker.Text.Length == 0)
                {
                    // Further letters extend this existing word; capture it whole on the next edit.
                    inspectEdit = true; typedSinceReset = 0; deletionEdit = false;
                }
                Status?.Invoke((changed ? "Исправлено" : "Проверено") +
                    $" • {watch.ElapsedMilliseconds} мс • в очереди {tracker.Text.Length} символов");
                await Task.Delay(30, cts.Token); // Let the target receive our input batch.
            }
        }
        catch (OperationCanceledException)
        {
            if (!cts.IsCancellationRequested)
            {
                allowRestart = false;
                Status?.Invoke("Модель не ответила вовремя. Следующий ввод повторит проверку очереди.");
            }
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or System.Text.Json.JsonException or
            InvalidOperationException or ObjectDisposedException)
        {
            Fault?.Invoke(e);
            allowRestart = false;
            if (!disposed && !cts.IsCancellationRequested) Status?.Invoke("Нет ответа модели. Проверьте локальную модель в настройках.");
        }
        finally
        {
            if (ReferenceEquals(pending, cts)) pending = null;
            if (allowRestart) Schedule();
        }
    }

    internal async void Undo()
    {
        if (editUndo is { } edited)
        {
            await UndoEditedWord(edited);
            return;
        }
        var entry = undo;
        if (entry == null) { Status?.Invoke("Отменять нечего: после замены уже изменился ввод или фокус."); return; }
        try
        {
            // Wait for release of the global hotkey before injecting text.
            for (var i = 0; i < 20 && Native.ModifiersDown(); i++) await Task.Delay(25);
            var target = await Task.Run(() => TextTarget.Capture(entry.Target.Window, entry.Replacement + entry.Tail, Settings, ownTest: true));
            if (disposed || revision != entry.Revision || target == null || !entry.Target.SameField(target) ||
                Native.ModifiersDown() || Native.GetForegroundWindow() != target.Window ||
                !Native.SameCaret(target.Caret, Native.GetGui(target.Window)))
            { Status?.Invoke("Отмена пропущена: поле или курсор изменились."); return; }
            pending?.Cancel();
            var ok = Native.ReplaceBeforeTail(entry.Replacement.Length, entry.Original, entry.Tail);
            Reset();
            if (ok) AllowInjectedInputToSettle(entry.Replacement.Length, entry.Original.Length, 2 * entry.Tail.Length);
            if (!ok) { Settings = Settings with { Enabled = false }; PausedAfterInputFailure?.Invoke(); }
            Status?.Invoke(ok ? "Последняя замена отменена" : "Windows заблокировала отмену. Проверьте текст; корректор на паузе.");
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        { if (!disposed) Status?.Invoke("Отмена недоступна."); }
    }

    private async Task UndoEditedWord(EditUndoEntry entry)
    {
        try
        {
            for (var i = 0; i < 20 && Native.ModifiersDown(); i++) await Task.Delay(25);
            var current = await Task.Run(() => TextTarget.CaptureWord(entry.Original.Target.Window, Settings, entry.CaretOffset == 0));
            if (disposed || revision != entry.Revision || current == null || !entry.Original.Target.SameField(current.Target) ||
                current.Edit.Word != entry.Replacement || current.Edit.CaretOffset != entry.CaretOffset ||
                current.Edit.Before != entry.Original.Edit.Before || current.Edit.After != entry.Original.Edit.After ||
                Native.ModifiersDown() || !Native.SameCaret(current.Target.Caret, Native.GetGui(current.Target.Window)))
            { Status?.Invoke("Отмена пропущена: слово или курсор изменились."); return; }
            var ok = Native.ReplaceWordAtCaret(entry.Replacement.Length, entry.CaretOffset,
                entry.Original.Edit.Word, entry.Original.Edit.CaretOffset);
            Reset();
            if (ok) AllowInjectedInputToSettle(entry.Replacement.Length, entry.Original.Edit.Word.Length);
            if (!ok) { Settings = Settings with { Enabled = false }; PausedAfterInputFailure?.Invoke(); }
            Status?.Invoke(ok ? "Последняя замена слова отменена" : "Windows заблокировала отмену слова.");
        }
        catch (InvalidOperationException) { if (!disposed) Status?.Invoke("Отмена слова недоступна."); }
    }

    private void AllowInjectedInputToSettle(int removed, int inserted, int moved = 0)
    {
        // SendInput queues events; a browser can still be applying a long edit
        // after it returns. Wait for the exact verified text, without reinjecting.
        inputSettleUntil = Environment.TickCount64 + Math.Clamp((removed + inserted + moved) * 20, 750, 5000);
    }

    public void Dispose()
    {
        disposed = true;
        Reset();
        observer.Dispose();
        idleTimer.Dispose();
        client.Dispose();
        spellingHints.Dispose();
    }
}
