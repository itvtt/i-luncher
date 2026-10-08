using System.Text;
using System.Windows;
using System.Windows.Threading;
using TerminalNotes.Controls;
using TerminalNotes.Services;
using TerminalNotes.TerminalEngine;

namespace TerminalNotes;

public sealed partial class TerminalPane
{
    private NativeTerminalSurface? _nativeTerminalSurface;
    private TerminalEmulator? _nativeTerminalEmulator;

    private Exception? HandleNativeSurfaceInput(string input, bool isProgrammatic, long? sendId)
    {
        if (_disposed || _failed || !_ready) return new InvalidOperationException("The terminal is not ready.");
        return HandleTerminalInput(input, isProgrammatic, sendId);
    }

    private Exception? HandleTerminalInput(string input, bool isProgrammatic, long? sendId)
    {
        bool hasSendId = sendId.HasValue;
        long inputSendId = sendId.GetValueOrDefault();
        bool terminalResponse = TerminalInputCapture.IsTerminalResponse(input);
        if (!isProgrammatic && !terminalResponse) CancelIncompleteSavedPlanDispatches();
        if (hasSendId && !CanWriteSavedPlanDispatch(inputSendId, input))
            return new InvalidOperationException("The saved plan submission is no longer current.");
        if (IsUserInputBlocked && !isProgrammatic)
            return new InvalidOperationException("Terminal input is temporarily blocked.");
        if (!_usesNativeOpenCodeStatus) _activityTracker.ObserveUserInput(input);
        if (!isProgrammatic && !terminalResponse)
        {
            _autoResume?.ObserveLocalInput(input);
            _inputCapture.Observe(input);
        }
        try
        {
            if (hasSendId) PrepareSavedPlanDispatchSubmit(inputSendId, input);
            _session.Write(input);
            if (hasSendId) ObserveSavedPlanDispatchWrite(inputSendId, input);
            else if (!isProgrammatic && !terminalResponse) ObserveUserWriteForSavedPlanDraft(input);
            return null;
        }
        catch (Exception writeError)
        {
            if (hasSendId)
            {
                if (_savedPlanDispatches.TryGetValue(inputSendId, out SavedPlanDispatch? dispatch))
                {
                    CancelSavedPlanDispatch(dispatch, writeError);
                    _autoResume?.Suspend("플랜 전달 실패 · 자동 진행 멈춤");
                }
                else if (_pendingTextSends.Remove(inputSendId, out var failedCompletion))
                    failedCompletion.TrySetException(writeError);
            }
            return writeError;
        }
    }

    private void HandleTerminalSendTextAck(long id, Exception? error)
    {
        if (_pendingTextSends.Remove(id, out var completion))
        {
            if (error is not null) completion.TrySetException(error);
            else completion.TrySetResult();
        }
        if (error is not null && _savedPlanDispatches.TryGetValue(id, out SavedPlanDispatch? dispatch))
            CancelSavedPlanDispatch(dispatch, error);
        CompleteSavedPlanDispatch(id);
    }

    private void HandleNativeSurfaceDimensionsChanged(short columns, short rows)
    {
        if (_disposed || _failed) return;
        if (_usesNativeOpenCodeTui)
        {
            columns = (short)Math.Clamp((int)columns, NativeTerminalSurface.MinimumColumns, 500);
            rows = (short)Math.Clamp((int)rows, NativeTerminalSurface.MinimumRows, 500);
        }
        _pendingWebReadyDimensions = (columns, rows);
        if (_usesNativeOpenCodeTui && !_ready)
            _openCodeTuiInitialDimensions = (columns, rows);
        try
        {
            _nativeTerminalEmulator?.Resize(columns, rows);
            if (_ready)
            {
                ReportLifecycleStage("resize_requested");
                _session.RequestResize(columns, rows);
            }
            else TryStartTerminalWhenLaidOut();
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke("터미널 크기 변경: " + exception.Message);
            if (!_ready) ShowError(exception.Message);
        }
    }
}
