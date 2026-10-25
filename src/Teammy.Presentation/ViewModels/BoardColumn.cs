using Teammy.Domain;

namespace Teammy.Presentation.ViewModels;

public sealed record BoardColumn(string Title, WorkItemStatus Status, IReadOnlyList<WorkItem> Items)
{
    public string CountLabel => Items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
