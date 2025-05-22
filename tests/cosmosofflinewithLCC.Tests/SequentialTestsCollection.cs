using Xunit;

namespace cosmosofflinewithLCC.Tests
{
    [CollectionDefinition("SequentialTests", DisableParallelization = true)]
    public class SequentialTestsCollection
    {
        // This class has no code, and is never created. Its purpose is simply
        // to be the place to apply [CollectionDefinition] and all the
        // ICollectionFixture<> interfaces.
    }
}
