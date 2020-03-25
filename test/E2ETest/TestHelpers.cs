﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Tye.Hosting;
using Microsoft.Tye.Hosting.Model;
using Xunit.Abstractions;

namespace E2ETest
{
    public static class TestHelpers
    {
        private static readonly TimeSpan WaitForServicesTimeout = TimeSpan.FromSeconds(10);
        
        // https://github.com/dotnet/aspnetcore/blob/5a0526dfd991419d5bce0d8ea525b50df2e37b04/src/Testing/src/TestPathUtilities.cs
        // This can get into a bad pattern for having crazy paths in places. Eventually, especially if we use helix,
        // we may want to avoid relying on sln position.
        public static string GetSolutionRootDirectory(string solution)
        {
            var applicationBasePath = AppContext.BaseDirectory;
            var directoryInfo = new DirectoryInfo(applicationBasePath);

            do
            {
                var projectFileInfo = new FileInfo(Path.Combine(directoryInfo.FullName, $"{solution}.sln"));
                if (projectFileInfo.Exists)
                {
                    return projectFileInfo.DirectoryName;
                }

                directoryInfo = directoryInfo.Parent;
            }
            while (directoryInfo.Parent != null);

            throw new Exception($"Solution file {solution}.sln could not be found in {applicationBasePath} or its parent directories.");
        }
        
        public static async Task StartHostAndWaitForReplicasToStart(TyeHost host)
        {
            var startedTask = new TaskCompletionSource<bool>();
            var alreadyStarted = 0;
            var totalReplicas = host.Application.Services.Sum(s => s.Value.Description.Replicas);

            void OnReplicaChange(ReplicaEvent ev)
            {
                if (ev.State == ReplicaState.Started)
                {
                    Interlocked.Increment(ref alreadyStarted);
                }
                else if (ev.State == ReplicaState.Stopped)
                {
                    Interlocked.Decrement(ref alreadyStarted);
                }
                
                if (alreadyStarted == 0)
                {
                    startedTask.TrySetResult(true);
                }
            }

            var servicesStateObserver = host.Application.Services.Select(srv => srv.Value.ReplicaEvents.Subscribe(OnReplicaChange)).ToList();
            await host.StartAsync();

            using var cancellation = new CancellationTokenSource(WaitForServicesTimeout);
            try
            {
                using (cancellation.Token.Register(() => startedTask.TrySetCanceled()))
                {
                    await startedTask.Task;
                }
            }
            catch (TaskCanceledException)
            {
                await host.StopAsync();
                throw;
            }
            finally
            {
                foreach (var observer in servicesStateObserver)
                {
                    observer.Dispose();
                }
            }
        }

        public static async Task PurgeHostAndWaitForGivenReplicasToStop(TyeHost host, string[] replicas)
        {
            var stoppedTask = new TaskCompletionSource<bool>();
            var alreadyStopped = replicas.Length;
            
            void OnReplicaChange(ReplicaEvent ev)
            {
                if (replicas.Contains(ev.Replica.Name) && ev.State == ReplicaState.Stopped)
                {
                    Interlocked.Decrement(ref alreadyStopped);
                }
                
                if (alreadyStopped == 0)
                {
                    stoppedTask.TrySetResult(true);
                }
            }
            
            var servicesStateObserver = host.Application.Services.Select(srv => srv.Value.ReplicaEvents.Subscribe(OnReplicaChange)).ToList();
            await host.PurgeAsync();
            
            using var cancellation = new CancellationTokenSource(WaitForServicesTimeout);
            try
            {
                using (cancellation.Token.Register(() => stoppedTask.TrySetCanceled()))
                {
                    await stoppedTask.Task;
                }
            }
            finally
            {
                foreach (var observer in servicesStateObserver)
                {
                    observer.Dispose();
                }
            }
        }
    }
}
