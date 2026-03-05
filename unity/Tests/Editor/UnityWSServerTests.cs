using NUnit.Framework;
using UnityMCP.Editor;

namespace UnityMCP.Editor.Tests
{
    public class UnityWSServerTests
    {
        [Test]
        public void TestCompactSerialization()
        {
            var obj = new { success = true, msg = "test", empty = (string)null };
            string result = MCPHandler.Compact(obj);
            
            // Should exclude the null property
            Assert.AreEqual("{\"success\":true,\"msg\":\"test\"}", result);
        }

        [Test]
        public void TestPortSettings()
        {
            int originalPort = UnityMCPSettings.Port;
            try
            {
                UnityMCPSettings.Port = 9999;
                Assert.AreEqual(9999, UnityMCPSettings.Port);
            }
            finally
            {
                UnityMCPSettings.Port = originalPort;
            }
        }
    }
}