using Avalonia.Media;
using Corkboard.Core.Models.Board;

namespace Corkboard.ViewModels.MainPages;

/// <summary>
///     同一科目的作业共用一个区块。
///     <para>
///         <paramref name="Key" /> 是落盘用的原始科目（空串表示未分类），
///         <paramref name="Subject" /> 是显示名（未分类时是资源文案）——手动排序记的是前者。
///         <paramref name="CanReorder" /> 在构建时就定下来（回放期间为 <c>false</c>）：
///         列表项是重建出来的，回放状态一变整份列表就重建，所以不需要在模板里做相对源绑定。
///     </para>
///     <para>
///         <paramref name="SubjectFontSize" /> 是科目名的字号（设置里的「科目标题字号」）。
///         字号跟着列表项走而不是在模板里绑页面 ViewModel：模板的绑定上下文是列表项，
///         而列表项本来就是每次重建的，设置一改就跟着变。
///     </para>
/// </summary>
public sealed record BoardSubjectItem(
    string Key,
    string Subject,
    IReadOnlyList<BoardNoteItem> Notes,
    bool CanReorder,
    double SubjectFontSize);

/// <summary>
///     科目区块内的一条作业：内容是主体，另有截止日期一行小字。
///     <para>
///         <see cref="Html" /> 是内容的富文本片段，<see cref="ContentFontSize" /> 与
///         <see cref="ContentColor" /> 是画它时要补的默认样式（设置里的「内容默认样式」，
///         颜色为空表示跟随主题），模板直接交给 <c>RichTextBlock</c>。
///     </para>
///     <para>
///         <see cref="CanModify" /> 在构建时就定下来（回放期间为 <c>false</c>），
///         编辑与删除两个按钮共用它。
///     </para>
///     <para>
///         <see cref="DueText" /> 是截止日期的显示文案（没设过就是空串，整行不显示），
///         <see cref="IsOverdue" /> 只在过期时为真，模板拿它换一个警示色。
///     </para>
///     <para>
///         <paramref name="MetaFontSize" /> 是截止日期那一行用的字号（设置里的「作业条小字字号」）：
///         它原来写死 12，后排看不见。
///     </para>
/// </summary>
public sealed record BoardNoteItem(
    BoardNote Note,
    string Html,
    double ContentFontSize,
    Color? ContentColor,
    double MetaFontSize,
    bool CanModify,
    string DueText,
    bool IsOverdue)
{
    public bool HasContent => Html.Length > 0;
    public bool HasDueText => DueText.Length > 0;
}
