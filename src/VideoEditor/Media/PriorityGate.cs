using System.Collections.Generic;
using System.Threading.Tasks;

namespace VideoEditor.Media
{
    /// <summary>
    /// 한 번에 정해진 수만 들이는 문(SemaphoreSlim처럼 비동기로 기다림). 기다리는 것 가운데 급한 것(<c>urgent</c>)을 먼저 들이고, 같은 급끼리는 온 차례다.
    /// 썸네일 그리기 줄에서 쓴다 — [다시 읽기]로 더한 사용자 전환 효과가 이미 줄 선 기본 효과 64개 뒤에서 기다리지 않게.
    /// </summary>
    public sealed class PriorityGate
    {
        private readonly object _lock = new object();
        private readonly LinkedList<TaskCompletionSource<bool>> _urgent = new LinkedList<TaskCompletionSource<bool>>();
        private readonly LinkedList<TaskCompletionSource<bool>> _normal = new LinkedList<TaskCompletionSource<bool>>();
        private int _free;

        public PriorityGate(int count)
        {
            _free = count;
        }

        /// <summary>기다리는 수(급한 것 + 보통).</summary>
        public int Waiting
        {
            get
            {
                lock (_lock)
                    return _urgent.Count + _normal.Count;
            }
        }

        /// <summary>자리가 나면 들어간다. 다 쓰고 나서 <see cref="Release"/>.</summary>
        public Task WaitAsync(bool urgent = false)
        {
            lock (_lock)
            {
                if (_free > 0)
                {
                    _free--;
                    return Task.CompletedTask;
                }
                // 이어지는 일은 Release를 부른 스레드에서 돌지 않게(다른 그리기를 끝낸 스레드를 붙잡지 않게)
                var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                (urgent ? _urgent : _normal).AddLast(waiter);
                return waiter.Task;
            }
        }

        /// <summary>자리를 내놓는다 — 기다리는 것이 있으면 급한 것부터 하나를 들인다.</summary>
        public void Release()
        {
            TaskCompletionSource<bool> next;
            lock (_lock)
            {
                var queue = _urgent.Count > 0 ? _urgent : _normal.Count > 0 ? _normal : null;
                if (queue == null)
                {
                    _free++;
                    return;
                }
                next = queue.First.Value;
                queue.RemoveFirst();
            }
            next.SetResult(true);
        }
    }
}
