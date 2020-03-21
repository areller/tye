// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Reactive.Subjects;
using System.Threading;

namespace Microsoft.Tye.Hosting.Model
{
    public class ReplicaStatus
    {
        private long _currentState;
        
        public ReplicaStatus(Service service, string name)
        {
            Service = service;
            Name = name;

            _currentState = (long)ReplicaState.Added;
        }

        public string Name { get; }

        public IEnumerable<(int Port, string? Protocol)>? Ports { get; set; }

        public Service Service { get; }

        public Dictionary<object, object> Items { get; } = new Dictionary<object, object>();

        public Dictionary<string, string> Metrics { get; set; } = new Dictionary<string, string>();

        public ReplicaState CurrentState => (ReplicaState)Interlocked.Read(ref _currentState);

        public CancellationTokenSource StoppedTokenSource { get; } = new CancellationTokenSource();

        public void UpdateState(ReplicaState state)
        {
            Interlocked.Exchange(ref _currentState, (long)state);
        }
    }
}
