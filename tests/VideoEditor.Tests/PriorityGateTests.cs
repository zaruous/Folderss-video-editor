using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Media;
using Xunit;
using Xunit.Abstractions;

namespace VideoEditor.Tests
{
    /// <summary>썸네일 그리기 줄(<see cref="PriorityGate"/>): 정한 수만 들이고, 급한 것(사용자 전환 효과)을 줄 선 것보다 먼저.</summary>
    public class PriorityGateTests
    {
        private readonly ITestOutputHelper _out;

        public PriorityGateTests(ITestOutputHelper output)
        {
            _out = output;
        }

        [Fact]
        public async Task Waiters_UrgentFirst_ThenArrivalOrder()
        {
            var gate = new PriorityGate(1);
            await gate.WaitAsync();
            var a = gate.WaitAsync();
            var b = gate.WaitAsync();
            var urgent = gate.WaitAsync(true);
            var urgent2 = gate.WaitAsync(true);
            Assert.Equal(4, gate.Waiting);
            Assert.False(a.IsCompleted || b.IsCompleted || urgent.IsCompleted);
            gate.Release();
            await urgent.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(a.IsCompleted || urgent2.IsCompleted);
            gate.Release();
            await urgent2.WaitAsync(TimeSpan.FromSeconds(5));
            gate.Release();
            await a.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(b.IsCompleted);
            gate.Release();
            await b.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, gate.Waiting);
            // 모두 내놓으면 다시 바로 들어간다
            gate.Release();
            Assert.True(gate.WaitAsync().IsCompleted);
        }

        [Fact]
        public async Task NeverMoreThanTheCount_AtOnce()
        {
            var gate = new PriorityGate(3);
            var inside = 0;
            var most = 0;
            await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(async () =>
            {
                await gate.WaitAsync(i % 5 == 0);
                try
                {
                    var now = Interlocked.Increment(ref inside);
                    InterlockedMax(ref most, now);
                    await Task.Delay(5);
                }
                finally
                {
                    Interlocked.Decrement(ref inside);
                    gate.Release();
                }
            })));
            Assert.Equal(3, most);
            Assert.Equal(0, gate.Waiting);
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }

        [Fact]
        public async Task UserTransitionAddedLater_IsDrawnBeforeTheQueuedBuiltIns()
        {
            // [전환] 탭을 열어 기본 효과 64개가 줄 선 뒤 [다시 읽기]로 사용자 효과를 더한 흐름(#34 실측: 사용자 썸네일까지 89초).
            // 그리기 하나에 20ms, 셋씩 — 사용자 효과는 지금 그리는 셋 다음에 그린다
            var gate = new PriorityGate(3);
            var order = new List<string>();
            Task Draw(string name, bool urgent)
            {
                return Task.Run(async () =>
                {
                    await gate.WaitAsync(urgent);
                    try
                    {
                        lock (order)
                            order.Add(name);
                        await Task.Delay(20);
                    }
                    finally
                    {
                        gate.Release();
                    }
                });
            }
            var builtIns = Enumerable.Range(0, 64).Select(i => Draw("builtin" + i, false)).ToList();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (gate.Waiting < 61 && DateTime.UtcNow < deadline)
                await Task.Delay(1);
            var user = Draw("user", true);
            await Task.WhenAll(builtIns.Append(user));
            var position = order.IndexOf("user");
            _out.WriteLine("사용자 효과를 그린 차례: " + (position + 1) + "번째(모두 " + order.Count + ")");
            Assert.InRange(position, 3, 6);
        }
    }
}
