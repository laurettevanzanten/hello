﻿using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using System.Text.Json;
using System.Threading;
using System.Collections.Generic;
using PostStressTest.StateMachine;
using System.Linq;
using System.Diagnostics;

/// <summary>
/// Simple stress test program for the back-end server
/// </summary>
namespace PostStressTest
{
   
    public class AgentTaskList : List<(BasicAgent agent, CancellationTokenSource cancelSource, Task task)>
    {
    }

    class Program
    {
        static void Main(string[] args)
        {
            //const string endPoint =  "https://rocky-basin-09452.herokuapp.com";
            const string endPoint = "http://localhost:3000";
            const int maxUsers = 50;
            const int maxTimeSeconds = 30;

            var log = new Log()
            {
                OutputSet = OutputChannel.Debug | OutputChannel.Memory
            };

            var ioc = new IoC()
                        .Register<Random>()
                        .Register<Log>(log)
                        .Register(UserList.UserListIoCID, UserList.Credentials);

            var agentTaskList = CreateTaskList(ioc, maxUsers, endPoint);

            RunAgentTasks(agentTaskList, maxTimeSeconds);
            StopAgentTasks(agentTaskList);

            var messageCount = agentTaskList.Sum(tuple => tuple.agent.AgentContext.Resolve<int>(AgentFactory.MessageCountId));
            var ackCount = agentTaskList.Sum(tuple => tuple.agent.AgentContext.Resolve<int>(AgentFactory.AckCountId));
            var errors = agentTaskList.Select(tuple => tuple.agent.AgentContext.Resolve<List<string>>(AgentFactory.ErrorId));

            log.FlushCSV("stress-test.csv");
        }

        private static AgentTaskList CreateTaskList(IoC ioc, int agentCount, string endPoint, int taskIntervalMS = 10)
        {
            var agentTaskList = new AgentTaskList();
            var userlist = ioc.Resolve<(string user, string password)[]>(UserList.UserListIoCID);

            // create tasks for each user
            for (int i = 0; i < agentCount; i++)
            {
                var cancelSource = new CancellationTokenSource();
                var token = cancelSource.Token;
                var agent = AgentFactory.CreateHttpMessageAgent(ioc, endPoint, agentCount, userlist[i].user, userlist[i].password);

                agentTaskList.Add((
                    agent,
                    cancelSource,
                    Task.Run(async () =>
                    {
                        using (agent)
                        {
                            try
                            {
                                agent.Start();

                                while (agent.Phase == StateMachine.StatePhase.Started)
                                {
                                    agent.Update();
                                    await Task.Delay(taskIntervalMS, token);
                                }
                            }
                            catch (Exception e)
                            {
                                Debug.WriteLine("caught unexpected exception " + e);
                                throw (e);
                            }
                        }
                        agent.Stop();
                    })));
            }

            return agentTaskList;
        }

        static void RunAgentTasks(
            AgentTaskList agentTaskList, 
            int maxTimeSeconds,
            int intervalMs = 10)
        {
            var startTime = DateTime.Now;

            while (agentTaskList.Count > 0 
                && (maxTimeSeconds < 0 || (DateTime.Now - startTime).TotalSeconds < maxTimeSeconds))
            {
                for (int i = 0; i < agentTaskList.Count;)
                {
                    var agentTask = agentTaskList[i].task;

                    if (agentTask.Status == TaskStatus.Running || agentTask.Status == TaskStatus.WaitingForActivation)
                    {
                        i++;
                    }
                    else
                    {
                        agentTaskList.RemoveAt(i);
                    }
                }

                Thread.Sleep(intervalMs);
            }
        }

        static void StopAgentTasks(AgentTaskList agentTaskList)
        {
            bool isRunning(Task t) => t.Status == TaskStatus.Running || t.Status == TaskStatus.WaitingForActivation;

            while (agentTaskList.Any( tuple => isRunning(tuple.task)))
            {
                for (int i = 0; i < agentTaskList.Count; i++)
                {
                    var agentTask = agentTaskList[i].task;
                    
                    if (isRunning(agentTask))
                    {
                        agentTaskList[i].cancelSource.Cancel();
                    }
                }

                Thread.Sleep(250);
            }
        }
    }
}
