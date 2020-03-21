using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Tye.Hosting.Model;

namespace Microsoft.Tye.Hosting
{
    public class Hospital : IApplicationProcessor
    {
        private ConcurrentDictionary<string, State> _states;
        private HttpClient _httpClient;
        private ILogger _logger;

        public Hospital(ILogger logger)
        {
            _states = new ConcurrentDictionary<string, State>();
            _httpClient = new HttpClient();
            _logger = logger;
        }
        
        public Task StartAsync(Model.Application application)
        {
            foreach (var service in application.Services.Values)
            {
                service.Items[typeof(Subscription)] = service.ReplicaEvents.Subscribe(OnReplicaChanged);
            }

            foreach (var state in _states)
            {
                state.Value.Dispose();
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(Model.Application application)
        {
            foreach (var service in application.Services.Values)
            {
                if (service.Items.TryGetValue(typeof(Subscription), out var item) && item is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            return Task.CompletedTask;
        }

        private void OnReplicaChanged(ReplicaEvent replicaEvent)
        {
            if (replicaEvent.State == ReplicaState.Removed)
            {
                if (_states.TryRemove(replicaEvent.Replica.Name, out var stateToDispose))
                    stateToDispose.Dispose();
            }
            else if (replicaEvent.State == ReplicaState.Added)
            {
                _states.TryAdd(replicaEvent.Replica.Name, new State(replicaEvent.Replica, _httpClient, _logger));
            }
            else if (_states.TryGetValue(replicaEvent.Replica.Name, out var state))
            {
                state.Update(replicaEvent);
            }
        }

        class State : IDisposable
        {
            private ReplicaStatus _replica;
            private HttpClient _httpClient;
            private ILogger _logger;
            
            private CancellationTokenSource? _cts;
            private Task? _pollTask;

            private string? _endpoint;
            private TimeSpan _testInterval;
            private TimeSpan _bootPeriod;
            private TimeSpan _gracePeriod;
            private ReplicaState _currentState;
            private DateTime _lastStateChange;
            
            public State(ReplicaStatus replica, HttpClient httpClient, ILogger logger)
            {
                _replica = replica;
                _httpClient = httpClient;
                _logger = logger;
            }

            public void Update(ReplicaEvent @event)
            {
                switch (@event.State)
                {
                    case ReplicaState.Started:
                        Start();
                        break;
                    case ReplicaState.Stopped:
                        Stop();
                        break;
                }
            }

            private void Start()
            {
                _cts?.Cancel();

                if (_replica.Service.Description.Health != null)
                {
                    _cts = new CancellationTokenSource();

                    _endpoint = _replica.Service.Description.Health.Endpoint;
                    _testInterval = TimeSpan.FromSeconds(_replica.Service.Description.Health.TestInterval);
                    _bootPeriod = TimeSpan.FromSeconds(_replica.Service.Description.Health.BootPeriod);
                    _gracePeriod = TimeSpan.FromSeconds(_replica.Service.Description.Health.GracePeriod);
                    
                    _currentState = ReplicaState.Started;
                    _lastStateChange = DateTime.UtcNow;
                    _pollTask = Task.Run(Poll);
                }
                else
                {
                    // if there is no health check in the description, service automatically moves to "Healthy"
                    MoveToHealthy();
                }
            }

            private void Stop()
            {
                _cts?.Cancel();
            }

            private async Task Poll()
            {
                while (!(_cts?.IsCancellationRequested ?? true))
                {
                    await Task.Delay(_testInterval);

                    var now = DateTime.UtcNow;
                    
                    var isGreen = await IsAllGreen();
                    var passedBootPeriod = now.Subtract(_lastStateChange) > _bootPeriod;
                    var passedGracePeriod = now.Subtract(_lastStateChange) > _gracePeriod;

                    switch ((_currentState, isGreen, passedBootPeriod, passedGracePeriod))
                    {
                        case (ReplicaState.Started, false, true, _):
                            Kill();
                            break;
                        case (ReplicaState.Started, true, _, _):
                            MoveToHealthy();
                            break;
                        case (ReplicaState.Sick, false, _, true):
                            Kill();
                            break;
                        case (ReplicaState.Sick, true, _, _):
                            MoveToHealthy();
                            break;
                        case (ReplicaState.Healthy, false, _, _):
                            MoveToSick();
                            break;
                    }
                }
            }

            private async Task<bool> IsAllGreen()
            {
                if (_replica.Ports is null)
                    return true;
                
                foreach (var port in _replica.Ports)
                {
                    var protocol = port.Protocol ?? "http";
                    var address = $"{protocol}://localhost:{port.Port}{_endpoint}";
                    var res = await _httpClient.GetAsync(address);

                    if (!res.IsSuccessStatusCode)
                        return false;
                }

                return true;
            }

            private void MoveToHealthy()
            {
                _logger.LogInformation("replica {name} is moving to an healthy status", _replica.Name);
                ChangeState(ReplicaState.Healthy);
            }

            private void MoveToSick()
            {
                _logger.LogInformation("replica {name} is moving to a sick status", _replica.Name);
                ChangeState(ReplicaState.Sick);
            }

            private void Kill()
            {
                _logger.LogInformation("killing '{state}' replica {name} because it didn't reach healthy status in time", _currentState, _replica.Name);
                _cts?.Cancel();
                _replica.StoppedTokenSource.Cancel();
            }

            private void ChangeState(ReplicaState state)
            {
                _replica.Service.ReplicaEvents.OnNext(new ReplicaEvent(state, _replica));
                _currentState = state;
                _lastStateChange = DateTime.UtcNow;
            }
            
            public void Dispose()
            {
                _cts?.Cancel();
            }
        }
        
        private class Subscription
        {
        }
    }
}
