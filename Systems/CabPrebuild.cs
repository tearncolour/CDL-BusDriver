using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Serial, low-priority managed CPU work. Shutdown skips queued jobs; an in-flight job may finish on its retiring queue.
    /// A queued job can be moved to the front by its tag (the bus the player has selected is the one about to be taken over).
    /// </summary>
    internal static class CabPrebuild
    {
        private sealed class Queue
        {
            internal readonly LinkedList<(object tag, Action work)> Jobs = new LinkedList<(object, Action)>();
            internal readonly SemaphoreSlim Ready = new SemaphoreSlim(0);
            internal volatile bool Retired;
        }

        private static readonly object s_Gate = new object();
        private static Queue s_Queue;

        internal static Task<T> Enqueue<T>(Func<T> work, object tag = null)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (s_Gate)
            {
                if (s_Queue == null)
                {
                    Queue started = new Queue();
                    try { new Thread(() => Run(started)) { IsBackground = true, Name = "Bus Driver model prebuild", Priority = ThreadPriority.BelowNormal }.Start(); }
                    catch { started.Ready.Dispose(); throw; }
                    s_Queue = started;
                }

                Queue queue = s_Queue;
                queue.Jobs.AddLast((tag, () =>
                {
                    if (queue.Retired) { done.TrySetCanceled(); return; }
                    try { done.TrySetResult(work()); }
                    catch (Exception error) { done.TrySetException(error); }
                }));
                queue.Ready.Release();
            }
            return Observe(done.Task);
        }

        /// <summary>Moves the queued jobs with this tag to the front, in their order; false when none is queued.</summary>
        internal static bool Promote(object tag)
        {
            if (tag == null) return false;
            lock (s_Gate)
            {
                if (s_Queue == null) return false;
                LinkedList<(object tag, Action work)> jobs = s_Queue.Jobs;
                var moved = new List<LinkedListNode<(object tag, Action work)>>();
                for (LinkedListNode<(object tag, Action work)> node = jobs.First; node != null; node = node.Next)
                    if (Equals(node.Value.tag, tag)) moved.Add(node);
                for (int i = moved.Count - 1; i >= 0; i--)
                {
                    jobs.Remove(moved[i]);
                    jobs.AddFirst(moved[i]);
                }
                return moved.Count > 0;
            }
        }

        internal static Task<T> Observe<T>(Task<T> task)
        {
            task.ContinueWith(failed => { _ = failed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }

        internal static void Shutdown()
        {
            lock (s_Gate)
            {
                Queue queue = s_Queue;
                s_Queue = null;
                if (queue == null) return;
                queue.Retired = true;
                queue.Ready.Release();
            }
        }

        private static void Run(Queue queue)
        {
            try
            {
                while (true)
                {
                    queue.Ready.Wait();
                    Action work = null;
                    lock (s_Gate)
                    {
                        if (queue.Jobs.Count > 0)
                        {
                            work = queue.Jobs.First.Value.work;
                            queue.Jobs.RemoveFirst();
                        }
                        else if (queue.Retired) break;
                    }
                    work?.Invoke();
                }
            }
            finally { queue.Ready.Dispose(); }
        }
    }
}
