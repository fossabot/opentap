using System.Threading;
using NUnit.Framework;

namespace OpenTap.UnitTests
{
    [TestFixture]
    public class TapThreadTest
    {

        static ThreadField<object> tf = new ThreadField<object>();
        static ThreadField<object> tf2 = new ThreadField<object>();
        [Test]
        public void TestThreadField()
        {
            tf.Value = 100;
            tf2.Value = 200;
            object value = null;
            Semaphore sem = new Semaphore(0,1);
            TapThread.Start(() =>
            {
                value = tf.Value;
                sem.Release();
            });
            sem.WaitOne();
            Assert.AreEqual(100, value);
        }

        /// <summary>
        /// Test two levels of children where the last level finishes last
        /// </summary>
        [Test]
        public void TestHierarchyCompleted1()
        {
            ManualResetEventSlim level1Completed = new ManualResetEventSlim();
            ManualResetEventSlim level2Completed = new ManualResetEventSlim();
            ManualResetEventSlim hierarchyCompletedCallbackCalled = new ManualResetEventSlim();
            bool level1CompletedBeforeHierarchyCompletedCallback = false;
            bool level2CompletedBeforeHierarchyCompletedCallback = false;
            TapThread.Start(() =>
            {
                TapThread.Start(() =>
                {
                    Thread.Sleep(100);
                    TapThread.Start(() =>
                    {
                        Thread.Sleep(100);
                        level2Completed.Set();
                    }, "Level2");
                    level1Completed.Set();
                }, "Level1");
            }, () =>
            {
                level1CompletedBeforeHierarchyCompletedCallback = level1Completed.IsSet;
                level2CompletedBeforeHierarchyCompletedCallback = level2Completed.IsSet;
                hierarchyCompletedCallbackCalled.Set();
            }, "Root");
            Thread.Sleep(200);
            Assert.IsTrue(hierarchyCompletedCallbackCalled.Wait(3000), "onHierarchyCompleted callback not called.");
            Assert.IsTrue(level1CompletedBeforeHierarchyCompletedCallback, "Child thread did not complete before onHierarchyCompleted callback.");
            Assert.IsTrue(level2CompletedBeforeHierarchyCompletedCallback, "Second level child thread did not complete before onHierarchyCompleted callback.");
        }

        /// <summary>
        /// Test two levels of children where the last level finishes first
        /// </summary>
        [Test]
        public void TestHierarchyCompleted2()
        {
            ManualResetEventSlim level1Completed = new ManualResetEventSlim();
            ManualResetEventSlim level2Completed = new ManualResetEventSlim();
            ManualResetEventSlim hierarchyCompletedCallbackCalled = new ManualResetEventSlim();
            bool level1CompletedBeforeHierarchyCompletedCallback = false;
            bool level2CompletedBeforeHierarchyCompletedCallback = false;
            TapThread.Start(() =>
            {
                TapThread.Start(() =>
                {
                    TapThread.Start(() =>
                    {
                        level2Completed.Set();
                    }, "Level2");
                    Thread.Sleep(100);
                    level1Completed.Set();
                }, "Level1");
            }, () =>
            {
                level1CompletedBeforeHierarchyCompletedCallback = level1Completed.IsSet;
                level2CompletedBeforeHierarchyCompletedCallback = level2Completed.IsSet;
                hierarchyCompletedCallbackCalled.Set();
            }, "Root");
            Thread.Sleep(200);
            Assert.IsTrue(hierarchyCompletedCallbackCalled.Wait(30 * 1000), "onHierarchyCompleted callback not called.");
            Assert.IsTrue(level1CompletedBeforeHierarchyCompletedCallback, "Child thread did not complete before onHierarchyCompleted callback.");
            Assert.IsTrue(level2CompletedBeforeHierarchyCompletedCallback, "Second level child thread did not complete before onHierarchyCompleted callback.");
        }
    }
}
