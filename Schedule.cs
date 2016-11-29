using System;
using System.Threading;
using System.Collections.Generic;
using MainLoop = TinyMUD.Loop;

namespace TinyMUD
{
	public sealed class Schedule : IDisposable
	{
		#region 自动转换Action类型
		public struct Callback
		{
			public Action<Schedule> Action
			{
				get { return action; }
			}

			private Action<Schedule> action;

			public static implicit operator Callback(Action action)
			{
				return new Callback {action = schedule => action()};
			}

			public static implicit operator Callback(Action<Schedule> action)
			{
				return new Callback {action = action};
			}
		}
		#endregion

		public int Time;
		public bool Loop;
		public object Value;
		public Callback Action;

		#region 计时器索引管理
		private static int total = 0;
		private static int freelistcross = 0;
		private static readonly Stack<int> freelist = new Stack<int>();
		private static readonly Stack<int> freelist_async = new Stack<int>();

		private static int Acquire()
		{
			if (MainLoop.IsCurrent)
			{
				if (freelist.Count != 0)
					return freelist.Pop();
				if (Interlocked.CompareExchange(ref freelistcross, 0, 1) == 1)
				{
					lock (freelist_async)
					{
						while (freelist_async.Count != 0)
							freelist.Push(freelist_async.Pop());
					}
				}
				if (freelist.Count != 0)
					return freelist.Pop();
			}
			else
			{
				lock (freelist_async)
				{
					if (freelist_async.Count != 0)
						return freelist_async.Pop();
				}
			}
			return Interlocked.Increment(ref total);
		}

		private static void Release(int index)
		{
			if (index == 0)
				throw new ObjectDisposedException(typeof(Schedule).FullName);

			if (MainLoop.IsCurrent)
			{
				freelist.Push(index);
			}
			else
			{
				lock (freelist_async)
				{
					freelist_async.Push(index);
				}
				Interlocked.Exchange(ref freelistcross, 1);
			}
		}
		#endregion

		private static readonly SortedDictionary<Index, Schedule> schedules = new SortedDictionary<Index, Schedule>(Comparer.Default);

		private int _start;
		private Index _index;

		private struct Index
		{
			public int index;
			public long elapsed;
		}

		private class Comparer : IComparer<Index>
		{
			public int Compare(Index x, Index y)
			{
				long result = x.elapsed - y.elapsed;
				if (result < 0)
					return -1;
				if (result > 0)
					return 1;
				return x.index - y.index;
			}

			public static readonly IComparer<Index> Default = new Comparer();
		}

		public Schedule()
		{
			Time = 0;
			Loop = false;
			_start = 0;
			_index.elapsed = -1;
			_index.index = Acquire();
		}

		~Schedule()
		{
			Dispose(false);
		}

		public bool IsRunning
		{
			get { return _start != 0; }
		}

		public override int GetHashCode()
		{
			if (_index.index == 0)
				throw new ObjectDisposedException(typeof(Schedule).FullName);

			return _index.index;
		}

		public void Start()
		{
			if (_index.index == 0)
				throw new ObjectDisposedException(typeof(Schedule).FullName);

			if (Time < 0)
				Time = 0;

			if (Interlocked.CompareExchange(ref _start, 1, 0) == 0)
			{
				_index.elapsed = Clock.Elapsed + Time * 1000;
				if (MainLoop.IsCurrent)
				{
					if (Interlocked.CompareExchange(ref _start, 2, 1) == 1)
						schedules.Add(_index, this);
				}
				else
				{
					MainLoop.Execute(() =>
					{
						if (Interlocked.CompareExchange(ref _start, 2, 1) == 1)
							schedules.Add(_index, this);
					});
				}
			}
		}

		public void Close()
		{
			((IDisposable)this).Dispose();
		}

		internal static void Update(List<Schedule> list)
		{
			long now = Clock.Elapsed;
			while (true)
			{
				Schedule schedule;
				var iterator = schedules.GetEnumerator();
				using (iterator)
				{
					if (!iterator.MoveNext())
						break;
					schedule = iterator.Current.Value;
				}
				if (schedule._index.elapsed > now)
					break;
				if (Interlocked.Exchange(ref schedule._start, 0) != 0)
					schedules.Remove(schedule._index);
				if (schedule.Loop)
				{
					if (Interlocked.CompareExchange(ref schedule._start, 2, 0) == 0)
					{
						schedule._index.elapsed += schedule.Time * 1000;
						schedules.Add(schedule._index, schedule);
					}
				}
				list.Add(schedule);
			}
		}

		void IDisposable.Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		private void Dispose(bool disposing)
		{
			Index index = _index;
			Release(Interlocked.Exchange(ref _index.index, 0));
			if (disposing)
			{
				if (Interlocked.Exchange(ref _start, 0) != 0)
				{
					if (MainLoop.IsCurrent)
					{
						schedules.Remove(index);
					}
					else
					{
						MainLoop.Execute(() =>
						{
							schedules.Remove(index);
						});
					}
				}
			}
		}
	}
}
