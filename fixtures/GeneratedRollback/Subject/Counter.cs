namespace Subject;

using System.Collections.Generic;
using System.Threading.Tasks;

public static partial class Counter
{
    [Zomp.SyncMethodGenerator.CreateSyncVersion(OmitNullableDirective = true)]
    private static async ValueTask<int> ReadAsync(List<string> items)
    {
        await Task.CompletedTask;
        return items.Count;
    }

    public static int Count(List<string> items) => Read(items);
}
