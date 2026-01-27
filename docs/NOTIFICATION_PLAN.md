MyAiHelper Task Completion Notification Plan (Scheme A)

1) New files
- Models/NotificationItem.cs
- Models/NotificationKind.cs
- Services/NotificationService.cs
- Services/TaskCompletionDetector.cs
- Views/NotificationPopupWindow.xaml
- Views/NotificationPopupWindow.xaml.cs
- Views/NotificationCenterView.xaml (optional UserControl) or integrate in MainWindow.xaml
- ViewModels/NotificationCenterViewModel.cs (optional; can live in MainWindowViewModel if small)

2) Existing files to modify
- Models/AppSettings.cs (persist notification history, optional)
- Services/TerminalService.cs (inject claude wrapper and completion sentinel)
- ViewModels/TerminalTabViewModel.cs (attach TaskCompletionDetector and raise completion events)
- ViewModels/MainWindowViewModel.cs (own NotificationService, expose notification list)
- MainWindow.xaml (notification center UI and entry point)
- MainWindow.xaml.cs (wire popup window creation if not pure MVVM)

3) Component designs

3.1 NotificationItem model
- Fields
  - Id: Guid
  - TabId: string
  - TabName: string (snapshot for display)
  - Title: string (e.g., "Task Completed")
  - Message: string (short summary, maybe last line or command)
  - Kind: NotificationKind (Info, Success, Warning, Error)
  - CreatedAtUtc: DateTime
  - IsRead: bool
  - Source: string (e.g., "Claude")
  - Payload: string? (optional JSON for extra metadata)
- Notes
  - Keep immutable fields set at creation to avoid UI flicker.
  - TabId is the navigation key for "click to jump".

3.2 NotificationService
- Responsibilities
  - Maintain ObservableCollection<NotificationItem> for UI binding.
  - AddNotification(...) with throttling/de-dup and max retention (e.g., 200).
  - MarkRead(id), MarkAllRead(), ClearAll(), Remove(id).
  - Raise NotificationAdded event for popup trigger.
  - Persist/load history via ConfigService (optional).
- Threading
  - All collection mutations on UI dispatcher.
  - Queue background events safely (ConcurrentQueue + DispatcherTimer).
- API sketch
  - Add(NotificationItem item)
  - IReadOnlyList<NotificationItem> Items { get; }
  - event Action<NotificationItem> NotificationAdded

3.3 TaskCompletionDetector
- Responsibilities
  - Accept output chunks, buffer to full lines.
  - Strip ANSI escape sequences for reliable matching.
  - Detect completion sentinels injected into shell.
  - Emit TaskCompleted event with metadata (tabId, exitCode, rawLine).
- Internal state
  - StringBuilder lineBuffer
  - Regex for ANSI: \x1B\[[0-?]*[ -/]*[@-~]
  - Regex for sentinel: ^\[\[CLAUDE_DONE:(?<tab>[^:]+):(?<code>-?\d+)\]\]$
  - Optional heuristic fallback (disabled by default).
- Integration point
  - TerminalTabViewModel.OnTerminalOutput feeds detector BEFORE WebView2 echo.
  - On TaskCompleted: set IsRunning=false (or IsTaskRunning=false), notify MainWindowViewModel.

3.4 NotificationPopupWindow
- WPF Window
  - WindowStyle=None, AllowsTransparency=true, Topmost=true, ShowInTaskbar=false
  - ShowActivated=false to avoid focus stealing
  - Position: right-top corner with margin; stack multiple popups by vertical offset
  - Auto-close after N seconds unless hovered
  - Click action: invoke callback to select tab and mark notification read
- Visuals
  - Use existing "cyber" style: neon borders, gradient glow, subtle scan-line animation
  - Animation: fade/slide in, fade/slide out

3.5 Notification center UI integration
- UI placement
  - Right side drawer/panel in MainWindow.xaml to match existing sci-fi UI.
  - Toggle button with unread count badge.
- Content
  - ItemsControl/CollectionViewSource for sorting by CreatedAtUtc desc.
  - Visual states for read/unread.
  - Actions: "Mark all read", "Clear", per-item "Open tab".
- Binding
  - ItemsSource bound to NotificationService.Items
  - Commands in MainWindowViewModel or NotificationCenterViewModel.

4) Implementation steps (ordered)
1. Add NotificationItem + NotificationKind in Models.
2. Add TaskCompletionDetector in Services (unit test if feasible).
3. Add NotificationService in Services (thread-safe add + event).
4. Integrate detector into TerminalTabViewModel:
   - Create detector per tab, feed output chunks.
   - On completion, raise event to MainWindowViewModel.
5. Modify TerminalService.Start to inject claude wrapper + sentinel.
6. Extend MainWindowViewModel:
   - Own NotificationService
   - Subscribe to tab completion events and add NotificationItem
   - Expose unread count and commands
7. Implement NotificationPopupWindow and popup manager logic.
8. Integrate notification center UI into MainWindow.xaml.
9. Optional: persist notifications in AppSettings.

5) Task completion detection rules (Claude output)
- Primary rule: sentinel line emitted by wrapper.
  - Unique string to avoid collisions: [[CLAUDE_DONE:<tabId>:<exitCode>]]
- Wrapper injection (PowerShell)
  - Send once per session before running claude:
    - $cmd = (Get-Command claude).Source
    - function claude { & $cmd @Args; $code=$LASTEXITCODE; Write-Output "[[CLAUDE_DONE:<tabId>:$code]]"; return $code }
- AutoRunClaude path
  - Inject wrapper, then run "claude ..." as currently.
- Fallback heuristics (optional, off by default)
  - Detect prompt return + no active output for N seconds.
  - Match known completion phrases: "done", "task complete" (high false positive risk).
  - Only enable if user toggles "Heuristic mode".
