using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Corkboard.Core.Abstraction.Services;
using Corkboard.Core.Controls;
using Corkboard.Core.Enums;
using Corkboard.Core.Enums.Configs;
using Corkboard.Core.Models.Board;
using Corkboard.Core.Models.SubConfigs.Board;
using Corkboard.Core.Services.Board;
using Corkboard.Core.Services.Config;
using CR = Corkboard.Core.Langs.Common.Resources;

namespace Corkboard.ViewModels.SettingsPages;

/// <summary>
///     作业板设置页的 ViewModel：名称与科目走草稿后提交，作业类型与字段直接改领域服务的集合，
///     内容的默认字号与颜色走草稿 + 防抖。
///     <para>
///         类型与科目存在 <c>board.json</c>（业务数据），由 <c>BoardService</c> 显式落盘，
///         所以这里没有配置管道那种「集合元素不会被自动保存」的问题：改完喊一声
///         <see cref="SaveAll" /> 就行。名称与科目这两个输入框除了失焦，还会在停手 400ms 后
///         自动提交（用户不用按回车），其余输入框靠 <c>LostFocus</c>。
///     </para>
/// </summary>
public partial class BoardSettingsPageViewModel : ViewModelBase
{
    private readonly IBoardService _boardService;
    private readonly MainConfigHandler _configHandler;
    private readonly BoardCleanupService _cleanupService;

    public BoardSettingsPageViewModel(
        MainConfigHandler configHandler,
        IBoardService boardService,
        BoardCleanupService cleanupService)
        : base(configHandler)
    {
        _boardService = boardService;
        _configHandler = configHandler;
        _cleanupService = cleanupService;
        BoardNameDraft = Board.BoardName;
        SubjectsDraft = string.Join("，", _boardService.Subjects);
        _defaultFontSizeDraft = (decimal)BoardContentStyle.ClampFontSize(Board.DefaultContentFontSize);
        _subjectFontSizeDraft = (decimal)BoardTextStyle.ClampSubjectFontSize(Board.SubjectFontSize);
        _noteMetaFontSizeDraft = (decimal)BoardTextStyle.ClampMetaFontSize(Board.NoteMetaFontSize);
        _blockMinWidthDraft = (decimal)BoardBlockStyle.ClampMinItemWidth(Board.BlockMinWidth);
        _cleanupTimeDraft = TimeDraft(Board.CleanupHour, Board.CleanupMinute);

        // 老版本的 settings.json 里没有色板字段（或者被手改脏了），进页面时先在原地收拾干净，
        // 否则浮窗上会出现透明/重复的色块。
        Board.NormalizePalette();

        // 清理时刻只在提交时夹一次，但越界值一旦落盘就会一直算错（比如 25 点），
        // 所以进页面也先夹一次。
        CommitCleanup();
        ReloadPaletteItems();
    }

    public BoardSettingsConfig Board => Config.BoardSettings;

    /// <summary>
    ///     排布（展示设置）下拉项。主页面顶部不放切换器，排布只在这里选；
    ///     排序方式已经搬到主界面标题栏的排序按钮上，所以这里没有排序项。
    /// </summary>
    public IReadOnlyList<BoardLayoutOption> LayoutOptions { get; } =
    [
        new(BoardLayoutMode.SingleColumn, CR.Board_Layout_SingleColumn),
        new(BoardLayoutMode.Block, CR.Board_Layout_Block)
    ];

    /// <summary>
    ///     作业板名称的输入草稿。**不直接绑配置**：配置每变一次就落一次盘（见 ConfigHandlerBase），
    ///     双向绑到配置会让每敲一个字符写一遍 settings.json。
    /// </summary>
    [ObservableProperty] private string _boardNameDraft;

    /// <summary>科目的输入草稿，逗号分隔。</summary>
    [ObservableProperty] private string _subjectsDraft;

    /// <summary>
    ///     内容默认字号的输入草稿。**不直接双向绑配置**：NumericUpDown 每动一下值就落一次盘
    ///     （<c>ConfigHandlerBase</c> 没有防抖），所以先存草稿，失焦 / 离开页面时再提交。
    /// </summary>
    [ObservableProperty] private decimal? _defaultFontSizeDraft;

    /// <summary>
    ///     区块最小宽度的输入草稿（DIP）。**不直接双向绑配置**：理由同字号，
    ///     先存草稿，失焦 / 离开页面时再提交。
    /// </summary>
    [ObservableProperty] private decimal? _blockMinWidthDraft;

    /// <summary>科目标题字号的输入草稿。理由同上：NumericUpDown 一路动一路写盘。</summary>
    [ObservableProperty] private decimal? _subjectFontSizeDraft;

    /// <summary>作业条小字（类型与字段值、截止日期）字号的输入草稿。</summary>
    [ObservableProperty] private decimal? _noteMetaFontSizeDraft;

    /// <summary>取色器停手多久才把颜色写进配置。拖光谱时每帧都在变，写盘要攒一攒。</summary>
    private static readonly TimeSpan ColorCommitDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>
    ///     名称与科目这两个输入框停手多久才提交。它们同样「一敲就变」，
    ///     而提交就意味着落一次盘，所以也要攒一攒——用户因此不用按回车。
    /// </summary>
    private static readonly TimeSpan TextCommitDelay = TimeSpan.FromMilliseconds(400);

    private Color? _defaultColorDraft;
    private DispatcherTimer? _defaultColorCommitTimer;

    private DispatcherTimer? _boardNameCommitTimer;
    private DispatcherTimer? _subjectsCommitTimer;

    /// <summary>
    ///     默认颜色的取色器草稿。理由同字号：每帧都写配置会把 <c>settings.json</c> 写爆。
    /// </summary>
    public Color DefaultColorDraft
    {
        get => _defaultColorDraft ?? Board.DefaultContentColor ?? ResolveThemeForeground();
        set
        {
            if (_defaultColorDraft == value)
                return;

            _defaultColorDraft = value;
            OnPropertyChanged();
            ScheduleColorCommit(value);
        }
    }

    /// <summary>默认颜色是否跟随主题（配置里为空就是跟随），对应设置卡上的开关。</summary>
    public bool FollowThemeColor
    {
        get => Board.DefaultContentColor is null;
        set
        {
            if (value == FollowThemeColor)
                return;

            CancelColorCommit();

            if (value)
            {
                Board.DefaultContentColor = null;
            }
            else
            {
                // 从「跟随主题」切到自定义时，起点取主题当前的前景色——
                // 直接给黑色的话，深色模式下整块作业内容会看不见。
                _defaultColorDraft ??= ResolveThemeForeground();
                Board.DefaultContentColor = _defaultColorDraft;
                OnPropertyChanged(nameof(DefaultColorDraft));
            }

            // 取色器只在「自定义」时显示，换档位要通知这一条。
            OnPropertyChanged();
        }
    }

    /// <summary>主题资源里没有「前景色」这个设置项，只能按当前主题取一次。</summary>
    private static Color ResolveThemeForeground()
    {
        // 条件访问不能带 out 参数，所以先取实例再查资源。
        if (Application.Current is { } application
            && application.TryGetResource("TextFillColorPrimaryBrush", null, out var resource)
            && resource is ISolidColorBrush brush)
        {
            return brush.Color;
        }

        return Colors.Black;
    }

    private void ScheduleColorCommit(Color value)
    {
        _defaultColorCommitTimer?.Stop();

        _defaultColorCommitTimer = new DispatcherTimer { Interval = ColorCommitDelay };
        _defaultColorCommitTimer.Tick += (_, _) =>
        {
            CancelColorCommit();

            // 已经切回「跟随主题」了：别再把手里的颜色写回配置。
            if (!FollowThemeColor)
                Board.DefaultContentColor = value;
        };
        _defaultColorCommitTimer.Start();
    }

    private void CancelColorCommit()
    {
        _defaultColorCommitTimer?.Stop();
        _defaultColorCommitTimer = null;
    }

    /// <summary>把还压在草稿里的颜色立刻写进配置，并停掉待触发的防抖计时。</summary>
    public void FlushColorDraft()
    {
        CancelColorCommit();

        if (_defaultColorDraft is { } draft && !FollowThemeColor && Board.DefaultContentColor != draft)
            Board.DefaultContentColor = draft;
    }

    /// <summary>提交内容的默认样式（字号草稿 + 颜色草稿）。</summary>
    public void CommitContentDefaults()
    {
        var size = DefaultFontSizeDraft is { } draft
            ? BoardContentStyle.ClampFontSize((double)draft)
            : BoardContentStyle.ClampFontSize(Board.DefaultContentFontSize);

        if (Math.Abs(Board.DefaultContentFontSize - size) > double.Epsilon)
            Board.DefaultContentFontSize = size;

        // 草稿被夹过（手输 999 之类）时同步回界面，免得显示的和落盘的不是一个数。
        var normalized = (decimal)size;
        if (DefaultFontSizeDraft != normalized)
            DefaultFontSizeDraft = normalized;

        FlushColorDraft();
    }

    /// <summary>
    ///     提交两个字号草稿（科目标题、作业条小字）。手输越界值时夹回合法区间，
    ///     并把夹过的值同步回界面，免得显示的和落盘的不是一个数。
    /// </summary>
    public void CommitFontSizes()
    {
        var subject = SubjectFontSizeDraft is { } subjectDraft
            ? BoardTextStyle.ClampSubjectFontSize((double)subjectDraft)
            : BoardTextStyle.ClampSubjectFontSize(Board.SubjectFontSize);

        if (Math.Abs(Board.SubjectFontSize - subject) > double.Epsilon)
            Board.SubjectFontSize = subject;

        var subjectNormalized = (decimal)subject;
        if (SubjectFontSizeDraft != subjectNormalized)
            SubjectFontSizeDraft = subjectNormalized;

        var meta = NoteMetaFontSizeDraft is { } metaDraft
            ? BoardTextStyle.ClampMetaFontSize((double)metaDraft)
            : BoardTextStyle.ClampMetaFontSize(Board.NoteMetaFontSize);

        if (Math.Abs(Board.NoteMetaFontSize - meta) > double.Epsilon)
            Board.NoteMetaFontSize = meta;

        var metaNormalized = (decimal)meta;
        if (NoteMetaFontSizeDraft != metaNormalized)
            NoteMetaFontSizeDraft = metaNormalized;
    }

    /// <summary>
    ///     提交区块最小宽度。手输越界值时夹回合法区间，并把夹过的值同步回界面，
    ///     免得显示的和落盘的不是一个数。
    /// </summary>
    public void CommitBlockMinWidth()
    {
        var width = BlockMinWidthDraft is { } draft
            ? BoardBlockStyle.ClampMinItemWidth((double)draft)
            : BoardBlockStyle.ClampMinItemWidth(Board.BlockMinWidth);

        if (Math.Abs(Board.BlockMinWidth - width) > double.Epsilon)
            Board.BlockMinWidth = width;

        var normalized = (decimal)width;
        if (BlockMinWidthDraft != normalized)
            BlockMinWidthDraft = normalized;
    }

    /// <summary>
    ///     排布下拉选中项。配置里若是越界值或老版本的「通铺」，先按
    ///     <see cref="BoardLayoutModes.Normalize" /> 折算成界面上真的提供的两种，再匹配下拉项。
    /// </summary>
    public BoardLayoutOption SelectedLayoutOption
    {
        get => LayoutOptions.FirstOrDefault(option => option.Mode == BoardLayoutModes.Normalize(Board.LayoutMode))
               ?? LayoutOptions[0];
        set
        {
            if (value is not null)
                Board.LayoutMode = value.Mode;
        }
    }

    /// <summary>设置页所有编辑的收口：草稿提交 + 科目落盘。输入框失焦与离开页面时都会调。</summary>
    public void SaveAll()
    {
        CommitBoardName();
        CommitSubjects();
        CommitContentDefaults();
        CommitFontSizes();
        CommitBlockMinWidth();
        CommitCleanup();
        FlushPaletteCommit();
        _boardService.Save();

        // 草稿上面都提交过了，压在防抖里的计时就别再触发一次（重复提交会白写一遍盘）。
        CancelTextCommits();
    }

    /// <summary>把名称草稿写回配置。配置自己会落盘。</summary>
    public void CommitBoardName()
    {
        var name = BoardNameDraft.Trim();
        if (Board.BoardName != name)
            Board.BoardName = name;
    }

    /// <summary>
    ///     把科目草稿（逗号分隔，中英文逗号都认）写回领域服务。
    ///     返回是否真的变了：科目存在 <c>board.json</c> 里，没变就不用白落一次盘。
    /// </summary>
    public bool CommitSubjects()
    {
        var parsed = SubjectsDraft
            .Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.CurrentCulture)
            .ToList();

        if (parsed.SequenceEqual(_boardService.Subjects))
            return false;

        _boardService.Subjects.Clear();
        foreach (var subject in parsed)
            _boardService.Subjects.Add(subject);

        return true;
    }

    /// <summary>
    ///     名称草稿动一下就在 400ms 后提交：用户不用按回车，改完停手就生效。
    ///     提交会落盘，所以不能每敲一个字符写一次 <c>settings.json</c>。
    /// </summary>
    partial void OnBoardNameDraftChanged(string value)
    {
        // 构造期会把草稿初始化成配置里的名字，别为此空排一次提交。
        if (value.Trim() == Board.BoardName)
            return;

        _boardNameCommitTimer = ScheduleTextCommit(_boardNameCommitTimer, CommitBoardName);
    }

    /// <summary>科目草稿同理，只是提交完要显式落盘（科目在 <c>board.json</c> 里）。</summary>
    partial void OnSubjectsDraftChanged(string value)
    {
        _subjectsCommitTimer = ScheduleTextCommit(_subjectsCommitTimer, CommitSubjectsAndSave);
    }

    /// <summary>
    ///     排一次「停手后提交」：先把上一次的计时顶掉，再起一个新的。
    ///     新计时器由调用点存回字段——lambda 里不能捕获 <c>ref</c> 参数，所以这里不用引用传递。
    /// </summary>
    private DispatcherTimer ScheduleTextCommit(DispatcherTimer? pending, Action commit)
    {
        pending?.Stop();

        var timer = new DispatcherTimer { Interval = TextCommitDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            commit();
        };
        timer.Start();

        return timer;
    }

    /// <summary>科目变了才落盘。</summary>
    private void CommitSubjectsAndSave()
    {
        if (CommitSubjects())
            _boardService.Save();
    }

    /// <summary>停掉还压在防抖里的名称 / 科目提交（<see cref="SaveAll" /> 已经把草稿提交过了）。</summary>
    private void CancelTextCommits()
    {
        _boardNameCommitTimer?.Stop();
        _boardNameCommitTimer = null;
        _subjectsCommitTimer?.Stop();
        _subjectsCommitTimer = null;
    }

    #region 工具浮窗色板

    /// <summary>
    ///     工具浮窗上那排预设色，包成可绑定的行对象给设置页。<c>Color</c> 是结构体，
    ///     直接摆一个 <c>ObservableCollection&lt;Color&gt;</c> 的话取色器改不动它（见
    ///     <see cref="BoardPaletteColorItem" />）。
    ///     <para>
    ///         集合元素也不会被 <c>ConfigHandlerBase</c> 订阅（只订阅对象属性），
    ///         所以每次改动都要显式落盘。
    ///     </para>
    /// </summary>
    public ObservableCollection<BoardPaletteColorItem> PaletteColors { get; } = [];

    /// <summary>色板到 5 个上限了（「新增」按钮据此禁用），上限本身在 <see cref="BoardPalette" /> 里。</summary>
    public bool CanAddPaletteColor => BoardPalette.CanAdd(PaletteColors.Count);

    /// <summary>从配置读一次色板，灌进设置页的行对象。</summary>
    private void ReloadPaletteItems()
    {
        PaletteColors.Clear();
        foreach (var color in Board.ResolvePaletteColors())
            PaletteColors.Add(new BoardPaletteColorItem(color, OnPaletteColorItemChanged));

        OnPropertyChanged(nameof(CanAddPaletteColor));
    }

    /// <summary>某一个色块的取色器动了：防抖之后写回配置。</summary>
    private void OnPaletteColorItemChanged(BoardPaletteColorItem item, Color color)
    {
        var index = PaletteColors.IndexOf(item);
        if (index < 0)
            return;

        SchedulePaletteColorCommit(index, color);
    }

    /// <summary>再加一个预设色。</summary>
    [RelayCommand]
    private void AddPaletteColor()
    {
        if (!CanAddPaletteColor)
            return;

        // 新色从默认色板里挑第一个还没用过的，免得加出来跟已有的撞成一样。
        var used = PaletteColors.Select(item => item.Color).ToList();
        var candidate = BoardPalette.DefaultColors.FirstOrDefault(color => !used.Contains(color));

        PaletteColors.Add(new BoardPaletteColorItem(candidate, OnPaletteColorItemChanged));
        PersistPalette();
    }

    public void RemovePaletteColor(BoardPaletteColorItem item)
    {
        if (!PaletteColors.Remove(item))
            return;

        PersistPalette();
    }

    /// <summary>把某个预设色往左挪一格。</summary>
    public void MovePaletteColorUp(BoardPaletteColorItem item) => MovePaletteColor(item, -1);

    /// <summary>把某个预设色往右挪一格。</summary>
    public void MovePaletteColorDown(BoardPaletteColorItem item) => MovePaletteColor(item, 1);

    private void MovePaletteColor(BoardPaletteColorItem item, int offset)
    {
        var index = PaletteColors.IndexOf(item);
        var target = index + offset;

        if (index < 0 || target < 0 || target >= PaletteColors.Count)
            return;

        PaletteColors.Move(index, target);

        // 顺序也是配置的一部分，挪完要落盘。
        PersistPalette();
    }

    /// <summary>
    ///     取色器拖一次光谱会发上百次变更，攒一下再落盘。按色块下标分别计时，
    ///     这样连改多个色块时不会互相把对方的计时顶掉。
    /// </summary>
    private readonly Dictionary<int, DispatcherTimer> _paletteCommitTimers = [];

    /// <summary>色块颜色变更的入口：防抖之后把行对象的值写回配置。</summary>
    private void SchedulePaletteColorCommit(int index, Color color)
    {
        if (_paletteCommitTimers.Remove(index, out var pending))
            pending.Stop();

        var timer = new DispatcherTimer { Interval = ColorCommitDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _paletteCommitTimers.Remove(index);

            // 期间可能已经删过色块，下标要先确认还在范围内。
            if (index < PaletteColors.Count)
                PersistPalette();
        };

        _paletteCommitTimers[index] = timer;
        timer.Start();
    }

    /// <summary>把还压在防抖里的色板改动立刻落盘（离开页面 / 保存时调）。</summary>
    public void FlushPaletteCommit()
    {
        if (_paletteCommitTimers.Count == 0)
            return;

        foreach (var timer in _paletteCommitTimers.Values)
            timer.Stop();

        _paletteCommitTimers.Clear();
        PersistPalette();
    }

    /// <summary>把界面上的色板写回配置并落盘。</summary>
    private void PersistPalette()
    {
        Board.ApplyPaletteColors(PaletteColors.Select(item => item.Color));
        OnPropertyChanged(nameof(CanAddPaletteColor));
        _configHandler.Save();

        // 色板同时是富文本工具栏的颜色入口：库在造工具栏时读一次静态数组，这里跟着写一遍，
        // 之后新打开的表单就是新色板（已经开着的那个表单不会跟着变，重开一次即最新）。
        RichTextToolbarPalette.Apply(Board.ResolvePaletteColors());
    }

    #endregion

    #region 自动清理

    /// <summary>清理周期下拉项。按周清理时下面才会多出一个「星期几」。</summary>
    public IReadOnlyList<BoardCleanupFrequencyOption> CleanupFrequencyOptions { get; } =
    [
        new(BoardCleanupFrequency.Daily, CR.Settings_Board_CleanupFrequency_Daily),
        new(BoardCleanupFrequency.Weekly, CR.Settings_Board_CleanupFrequency_Weekly)
    ];

    /// <summary>「星期几」下拉项，序号与 <see cref="DayOfWeek" /> 同序。</summary>
    public IReadOnlyList<BoardCleanupWeekdayOption> CleanupWeekdayOptions { get; } =
    [
        new(DayOfWeek.Monday, CR.Board_Weekday_Monday),
        new(DayOfWeek.Tuesday, CR.Board_Weekday_Tuesday),
        new(DayOfWeek.Wednesday, CR.Board_Weekday_Wednesday),
        new(DayOfWeek.Thursday, CR.Board_Weekday_Thursday),
        new(DayOfWeek.Friday, CR.Board_Weekday_Friday),
        new(DayOfWeek.Saturday, CR.Board_Weekday_Saturday),
        new(DayOfWeek.Sunday, CR.Board_Weekday_Sunday)
    ];

    /// <summary>按周清理时才显示「星期几」。</summary>
    public bool IsWeeklyCleanup => Board.CleanupFrequency == BoardCleanupFrequency.Weekly;

    /// <summary>清理时刻的选择器草稿。理由同字号：先存草稿，失焦 / 离开页面时再提交。</summary>
    [ObservableProperty] private TimeSpan? _cleanupTimeDraft;

    /// <summary>
    ///     上一次清理干了什么。<b>只反映这一次进程里的清理</b>：历史结果没有落盘，
    ///     重开应用后这里会回到「还没清理过」。
    /// </summary>
    public string CleanupLastResultText
    {
        get
        {
            if (_cleanupService.LastResult is not { } result)
                return CR.Settings_Board_CleanupNeverRun;

            var text = result.Cleaned == 0
                ? CR.Settings_Board_CleanupResultNothing
                : string.Format(CR.Settings_Board_CleanupResultFormat, result.Cleaned);

            return $"{result.RanAt.LocalDateTime:t} · {text}";
        }
    }

    public BoardCleanupFrequencyOption SelectedCleanupFrequencyOption
    {
        get => CleanupFrequencyOptions.FirstOrDefault(option => option.Frequency == Board.CleanupFrequency)
               ?? CleanupFrequencyOptions[0];
        set
        {
            if (value is null || Board.CleanupFrequency == value.Frequency)
                return;

            Board.CleanupFrequency = value.Frequency;
            OnPropertyChanged(nameof(IsWeeklyCleanup));
        }
    }

    public BoardCleanupWeekdayOption SelectedCleanupWeekdayOption
    {
        get => CleanupWeekdayOptions.FirstOrDefault(option => option.Weekday == Board.CleanupWeekday)
               ?? CleanupWeekdayOptions[0];
        set
        {
            if (value is not null)
                Board.CleanupWeekday = value.Weekday;
        }
    }

    /// <summary>
    ///     提交清理时刻。手输 / 选出来的越界值在这里夹回合法区间，并把夹过的值同步回界面，
    ///     免得显示的和落盘的不是一个数。按 24 小时制存成「时 + 分」两个整数。
    /// </summary>
    public void CommitCleanup()
    {
        var (hour, minute) = ResolveTime(CleanupTimeDraft, Board.CleanupHour, Board.CleanupMinute);
        Board.CleanupHour = BoardCleanupDefaults.ClampHour(hour);
        Board.CleanupMinute = BoardCleanupDefaults.ClampMinute(minute);

        CleanupTimeDraft = TimeDraft(Board.CleanupHour, Board.CleanupMinute);
    }

    /// <summary>时间选择器的草稿 → 时与分；选择器被清空（<c>null</c>）就退回原值，而不是写 0 点。</summary>
    private static (int Hour, int Minute) ResolveTime(TimeSpan? draft, int fallbackHour, int fallbackMinute)
        => draft is { } value ? (value.Hours, value.Minutes) : (fallbackHour, fallbackMinute);

    private static TimeSpan TimeDraft(int hour, int minute) => new(hour, minute, 0);

    /// <summary>
    ///     用户点了「立即清理」：不看时刻，截止日期过了的作业当场清掉。
    ///     先把界面上的清理时刻提交掉，免得草稿还挂在输入框里没落盘。
    /// </summary>
    [RelayCommand]
    private void RunCleanupNow()
    {
        CommitCleanup();

        _cleanupService.Run(DateTimeOffset.Now);

        // 板子上的列表重建由 IBoardService.Changed 驱动；这里只管自己那行结果文字。
        OnPropertyChanged(nameof(CleanupLastResultText));
    }

    #endregion
}
