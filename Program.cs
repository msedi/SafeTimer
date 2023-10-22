using ipipe.Timers;

Console.WriteLine("Hello, World!");

SafeTimer timer = new(Timer_Handler);

timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(5));

void Timer_Handler(object? sender, SafeTimerCallbackEventArgs e)
{
    Console.WriteLine("   " + DateTime.Now.ToString());

    while (true)
    {
        if (e.CancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("    Timer cancelled");
            break;
        }
    }
}

Console.WriteLine("Waiting");

while (true)
{
    var key = Console.ReadKey();
    Console.WriteLine($"Key Pressed: {key.Key}");

    switch (key.Key)
    {
        case ConsoleKey.Q:
            Console.WriteLine("Stopping Timer");
            timer.Stop(true);
            break;

        case ConsoleKey.C:
            if (timer.IsStarted)
            {
                Console.WriteLine("Cancel Timer");
                timer.Stop(true);
            }
            else
            {
                Console.WriteLine("Timer is not running, no cancellation possible.");

            }
            break;

        case ConsoleKey.S:
            Console.WriteLine("Start Timer");
            timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(5));
            break;
    }
}

namespace ipipe.Timers
{
    /// <summary>
    /// A timer wrapper class that allows safe cancellation on object disposal and prevent reentrant calls to the
    /// timer handler.
    /// </summary>
    public sealed class SafeTimer : IDisposable
    {
        readonly object _syncRoot = new();
        readonly Timer _timer;
        bool _isDisposed = true;
        CancellationTokenSource _cts = new();
        long _isStarted = 0;

        // When entering the timer, we signal to other threads with the _isRunning flag, that 
        // they should not enter the handler and return immediately. Only if the _isRunning is set to 0, we can proceed.
        // _isRunning is also considered as WaitHandle to wait until the handler has returned as 
        long _isRunning = 0;

        private readonly SafeTimerCallback _handler;

        /// <summary>
        /// Returns if the timer has been started by using <see cref="Stop(bool, TimeSpan?)"/> or <see cref="Change(TimeSpan, TimeSpan)"/>.
        /// </summary>
        public bool IsStarted => Interlocked.Read(ref _isStarted) > 0;

        public SafeTimer(SafeTimerCallback callback)
        {
            _timer = new Timer(TimerCallback);
            _handler = callback ?? throw new ArgumentNullException(nameof(callback));
        }

        public void Start(TimeSpan dueTime, TimeSpan duration)
        {
            Change(dueTime, duration);
        }

        /// <summary>
        /// Changes the due time when the timer begins and sets the signaling interval.
        /// </summary>
        /// <param name="dueTime"></param>
        /// <param name="duration"></param>
        public void Change(TimeSpan dueTime, TimeSpan duration)
        {
            if (Interlocked.CompareExchange(ref _isStarted, 1, 0) == 1)
                return;

            // If the dueTime is infinite, the timer will never started which is considered as IsStarted=false
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                Interlocked.Exchange(ref _isStarted, 0);
                return;
            }

            _timer.Change(dueTime, duration);
        }

        /// <summary>
        /// Stops the timer from being revoked again.
        /// </summary>
        /// <param name="force">Forces the time to stop immediately. The timer callback  </param>
        public void Stop(bool force = false, TimeSpan? timeout = null)
        {
            // We try to stop the timer immediately by settings the dueTime to Infinite.
            Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            // If the handler is a long running method, stopping the timer with Change is not sufficient,
            // we also need to have some cancellation to signal the handler to cancel. This only works well
            // if the implementor of the handler is using the cancellation token.
            if (force)
            {
                lock (_syncRoot)
                    _cts.Cancel();
            }

            // Only if the handler returns, the _isRunning will be set to 0 again. 
            // If the method does not return we have a problem in every case and should cancel the whole application.
            if (timeout is null)
            {
                SpinWait.SpinUntil(() => Interlocked.Read(ref _isRunning) == 0);
            }
            else
            {
                if (!SpinWait.SpinUntil(() => Interlocked.Read(ref _isRunning) == 0, timeout.Value))
                    throw new TimeoutException($"Stopping the timer did not succeed. The handler could not be cancelled with {timeout.Value.TotalMilliseconds}ms");
            }
        }

        private void TimerCallback(object? state)
        {
            if (Interlocked.CompareExchange(ref _isRunning, 1, 0) == 1)
                return;

            try
            {
                // Do not let another timer event enter, when the timer even took to long and is not finished.
                _handler?.Invoke(this, new SafeTimerCallbackEventArgs(state, _cts.Token));
            }
            finally
            {
                Interlocked.Exchange(ref _isRunning, 0);

                // Only if a cancellation was requested, we need to reset the cancellation token
                // We try to use the reset, but if this doesn't work, we need to create a new cancellation token.
                if (_cts.IsCancellationRequested)
                {
                    if (!_cts.TryReset())
                    {
                        lock (_syncRoot)
                        {
                            _cts?.Dispose();
                            _cts = new();
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            GC.SuppressFinalize(this);

            Stop(true);

            _cts.Dispose();
        }
    }

    public sealed class SafeTimerCallbackEventArgs : EventArgs
    {
        public readonly object? State;
        public readonly CancellationToken CancellationToken;

        public SafeTimerCallbackEventArgs(object? state, CancellationToken cancellationToken)
        {
            State = state;
            CancellationToken = cancellationToken;
        }
    }

    public delegate void SafeTimerCallback(object sender, SafeTimerCallbackEventArgs e);
}
