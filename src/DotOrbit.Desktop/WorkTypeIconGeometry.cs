using Avalonia.Media;

namespace DotOrbit.Desktop;

internal static class WorkTypeIconGeometry
{
    public const string BacklogPath = "M3,5H6V8H3V5M9,5H21V8H9V5M3,11H6V14H3V11M9,11H21V14H9V11M3,17H6V20H3V17M9,17H21V20H9V17";
    public const string TaskPath = "M8,2H16V6H8ZM16,4H18C19.1,4 20,4.9 20,6V20C20,21.1 19.1,22 18,22H6C4.9,22 4,21.1 4,20V6C4,4.9 4.9,4 6,4H8";
    public const string ProjectPath = "M10,4H2C0.9,4 0,4.9 0,6V18C0,19.1 0.9,20 2,20H22C23.1,20 24,19.1 24,18V8C24,6.9 23.1,6 22,6H12L10,4";
    public const string CategoryPath = "M3,3H10V10H3V3M14,3H21V10H14V3M3,14H10V21H3V14M14,14H21V21H14V14";

    public static StreamGeometry Task { get; } = StreamGeometry.Parse(TaskPath);
    public static StreamGeometry Project { get; } = StreamGeometry.Parse(ProjectPath);
    public static StreamGeometry Category { get; } = StreamGeometry.Parse(CategoryPath);
}
