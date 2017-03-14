using System;
using Xunit;

namespace test.dotnet_xunit
{
    public class UnitTest1
    {
        [Fact]
        public void Passing()
        {
            Assert.True(true);
        }

        [Fact]
        public void Failing()
        {
            Assert.True(false);
        }
    }
}
