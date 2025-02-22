// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions;

namespace DevProxy
{
    public class InactivityTimer : IInactivityTimer
    {
        private Timer? _timer;
        private readonly TimeSpan _timeout;
        private static readonly TimeSpan InfiniteTimeout = TimeSpan.FromMilliseconds(-1);

        public InactivityTimer(long timeoutSeconds, Action timeoutAction)
        {
            _timeout = TimeSpan.FromSeconds(timeoutSeconds);
            Action action = timeoutAction ?? throw new ArgumentNullException(nameof(timeoutAction));
        
            _timer = new Timer(_ => action.Invoke(), null, _timeout, InfiniteTimeout);
        }

        public void Reset()
        {
            this._timer?.Change(_timeout, InfiniteTimeout);
        }

        public void Stop()
        {
            this._timer?.Dispose();
            _timer = null;
        }
    }
}