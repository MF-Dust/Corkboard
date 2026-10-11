using Corkboard.Core.Enums.Configs;
using Corkboard.Core.Models.Board;

namespace Corkboard.Core.Services.Board;

/// <summary>
///     先按设置排序，再合并同名科目；「手动排序」的科目顺序由 <paramref name="subjectOrder" /> 决定，
///     「按科目排序」的科目顺序由设置里的科目表 <paramref name="subjects" /> 决定，
///     其余排序方式仍由排序后首次出现的位置决定。
/// </summary>
public static class BoardNoteGrouping
{
    /// <param name="subjectOrder">
    ///     手动排序的科目先后（<c>BoardService.SubjectOrder</c>）。只有
    ///     <see cref="BoardSortMode.Manual" /> 会用到，其它排序方式忽略它。
    ///     不在表里的科目排在最后，彼此保持首次出现的相对顺序。
    /// </param>
    /// <param name="subjects">
    ///     设置里配置的科目表（<c>BoardService.Subjects</c>，即作业板设置里的「科目」）。
    ///     只有 <see cref="BoardSortMode.Subject" /> 会用到：按表里的先后排，
    ///     不在表里的科目（含「未分类」）排在最后，彼此按名称先后。表为空时退回按名称排。
    /// </param>
    public static IReadOnlyList<IGrouping<string, BoardNote>> Group(
        IEnumerable<BoardNote> notes, BoardSortMode sortMode,
        IReadOnlyList<string>? subjectOrder = null, IReadOnlyList<string>? subjects = null)
    {
        var sorted = sortMode switch
        {
            BoardSortMode.CreatedAscending => notes.OrderBy(note => note.CreatedAt),
            BoardSortMode.UpdatedDescending => notes.OrderByDescending(note => note.UpdatedAt),
            BoardSortMode.Subject => OrderByConfiguredSubject(notes, subjects),

            // 手动：区块顺序交给落盘的科目表，区块内保持加入顺序（Order 由 BoardService.Add 递增）。
            BoardSortMode.Manual => notes.OrderBy(note => note.Order).ThenBy(note => note.CreatedAt),
            _ => notes.OrderByDescending(note => note.CreatedAt)
        };

        var groups = sorted.GroupBy(note => note.Subject.Trim(), StringComparer.Ordinal).ToList();
        if (sortMode != BoardSortMode.Manual || subjectOrder is not { Count: > 0 })
        {
            return groups;
        }

        // OrderBy 是稳定排序：没记进顺序表的科目会整体落到后面，并保持原本的相对先后。
        return [.. groups.OrderBy(group => IndexOf(subjectOrder, group.Key))];
    }

    // 「按科目排序」按设置里的科目表排：表里的科目依次在前，表外的科目（含「未分类」）整体落到后面、彼此按名称。
    private static IOrderedEnumerable<BoardNote> OrderByConfiguredSubject(
        IEnumerable<BoardNote> notes, IReadOnlyList<string>? subjects)
    {
        if (subjects is not { Count: > 0 })
        {
            return notes.OrderBy(note => note.Subject.Trim(), StringComparer.CurrentCulture);
        }

        return notes.OrderBy(note => IndexOf(subjects, note.Subject.Trim()))
            .ThenBy(note => note.Subject.Trim(), StringComparer.CurrentCulture);
    }

    private static int IndexOf(IReadOnlyList<string> subjectOrder, string subject)
    {
        for (var i = 0; i < subjectOrder.Count; i++)
        {
            if (string.Equals(subjectOrder[i].Trim(), subject, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return int.MaxValue;
    }
}
