using System.Threading;
using Ruffles.Utils;
using Ruffles.Messaging;

namespace Ruffles.Collections
{
	// Operates on the same principle as the sliding window except overwrites and seeks are not allowed. It thus requires two heads.
	internal class ConcurrentCircularQueue<T>
	{
		private readonly int[] _indexes;
		private readonly int[] _sequences;
		private readonly T[] _array;
		private int _writeHead;
		private int _readHead;
		private bool sortBySequenceSupport;

		public int Count
		{
			get
			{
				return _writeHead - _readHead;
			}
		}

		public ConcurrentCircularQueue(int size, bool sortBySequenceSupport = false)
		{
			_array = new T[size];
			_indexes = new int[size];
			this.sortBySequenceSupport = sortBySequenceSupport;
			if (sortBySequenceSupport)
			{
				_sequences = new int[size];
				for (int i = 0; i < _sequences.Length; i++)
				{
					_sequences[i] = -1000;
				}
			}

			for (int i = 0; i < _indexes.Length; i++)
			{
				_indexes[i] = i;
			}
		}

		public void Enqueue(T element, bool sortBySequence = false, ushort sequence = 0)
		{
			while (!TryEnqueue(element, sortBySequence, sequence))
			{
				Thread.SpinWait(1);
			}
		}

		public T Dequeue()
		{
			while (true)
			{
				if (TryDequeue(out T element))
				{
					return element;
				}
			}
		}

		public bool TryEnqueue(T element, bool sortBySequence = false, int sequence = -1000)
		{
			// Cache writeHead and try to assign in a loop instead of pre incrementing writeHead in order to safe against buffer wraparounds

			while (true)
			{
				int positionWrite = _writeHead;
				int arrayIndexWrite = NumberUtils.WrapMod(positionWrite, _array.Length);

				if (_indexes[arrayIndexWrite] == _writeHead && Interlocked.CompareExchange(ref _writeHead, positionWrite + 1, positionWrite) == positionWrite)
				{
					if (sequence != -1000 && sortBySequence && Count > 0)
					{
						// Go from positionRead to positionWrite and if an element is bigger than sequence, that's where we will insert and update all the next ones to the right index
						bool done = false;
						/* string s1 = "";
						string s2 = ""; */
						lock ("sortLock")
						{
							for (int k = _readHead; k <= _writeHead; k++)
							{
								int arrayIndex = NumberUtils.WrapMod(k, _array.Length);

								//string s0 = _sequences[arrayIndex] + " ";

								if (_sequences[arrayIndex] != -1000 && SequencingUtils.Distance((ulong)_sequences[arrayIndex], (ulong)sequence, sizeof(ushort)) > 0)
								{
									if (k != _writeHead)
									{
										for (int i = _writeHead + 1; i > k; i--)
										{
											int previousAI = NumberUtils.WrapMod(i - 1, _array.Length);
											int aI = NumberUtils.WrapMod(i, _array.Length);

											/* if (i != _writeHead + 1)
												s1 = _sequences[aI] + " " + s1;
											s2 = _sequences[previousAI] + " " + s2; */

											_array[aI] = _array[previousAI];
											Thread.MemoryBarrier();
											_indexes[aI] = _indexes[previousAI] + 1;
											_sequences[aI] = _sequences[previousAI];
										}

										_array[arrayIndex] = element;
										Thread.MemoryBarrier();
										_sequences[arrayIndex] = sequence;

										/* Logging.LogInfo("Needs sorting: ... " + s0 + "| " + s1 + "*" + sequence + " (" + _readHead + " - " + _writeHead + ")");
										Logging.LogInfo("-----  Sorted: ... *" + sequence + " | " + s2 + " (" + _readHead + " - " + _writeHead + ")"); */

										done = true;
										break;
									}
								}
							}

							if (!done)
							{
								//Logging.LogInfo("- No sorting: " + s0 + s + sequence);

								_array[arrayIndexWrite] = element;
								Thread.MemoryBarrier();
								_indexes[arrayIndexWrite] = positionWrite + 1;
								_sequences[arrayIndexWrite] = sequence;
							}
						}
					}
					else
					{
						_array[arrayIndexWrite] = element;
						Thread.MemoryBarrier();
						_indexes[arrayIndexWrite] = positionWrite + 1;
						if (sortBySequence)
							_sequences[arrayIndexWrite] = sequence;
					}

					return true;
				}
				else if (_indexes[arrayIndexWrite] < positionWrite)
				{
					// Overflow, it cannot be assigned as a forward enqueue did not occur
					return false;
				}
			}
		}

		public bool TryDequeue(out T element)
		{
			// Cache readHead and try to read in a loop instead of pre incrementing readHead in order to safe against buffer wraparounds

			while (true)
			{
				int position = _readHead;

				int arrayIndex = NumberUtils.WrapMod(position, _array.Length);

				if (_indexes[arrayIndex] == _readHead + 1 && Interlocked.CompareExchange(ref _readHead, position + 1, position) == position)
				{
					lock ("sortLock")
					{
						element = _array[arrayIndex];
						_array[arrayIndex] = default(T);

						Thread.MemoryBarrier();
						_indexes[arrayIndex] = position + _array.Length;
						if (sortBySequenceSupport)
							_sequences[arrayIndex] = -1000;

						return true;
					}
				}
				else if (_indexes[arrayIndex] < position + 1)
				{
					element = default(T);

					return false;
				}
			}
		}
	}
}