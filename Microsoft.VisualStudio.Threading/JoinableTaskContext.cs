﻿//-----------------------------------------------------------------------
// <copyright file="JoinableTaskContext.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.VisualStudio.Threading {
	using System;
	using System.Collections.Concurrent;
	using System.Collections.Generic;
	using System.Collections.Specialized;
	using System.Diagnostics;
	using System.Linq;
	using System.Runtime.CompilerServices;
	using System.Threading;
	using System.Threading.Tasks;
	using JoinableTaskSynchronizationContext = Microsoft.VisualStudio.Threading.JoinableTask.JoinableTaskSynchronizationContext;
	using SingleExecuteProtector = Microsoft.VisualStudio.Threading.JoinableTaskFactory.SingleExecuteProtector;

	/// <summary>
	/// A common context within which joinable tasks may be created and interact to avoid deadlocks.
	/// </summary>
	/// <devremarks>
	/// Lots of documentation and FAQ on Joinable Tasks is available on OneNote: <![CDATA[
	/// http://devdiv/sites/vspe/prjbld/_layouts/OneNote.aspx?id=%2fsites%2fvspe%2fprjbld%2fOneNote%2fTeamInfo&wd=target%28VS%20Threading.one%7c46FEAAD0-0131-45EE-8C52-C9893F1FD331%2fThreading%20Rules%7cD0EEFAB9-99C0-4B8F-AA5F-4287DD69A38F%2f%29
	/// ]]>
	/// </devremarks>
	/// <remarks>
	/// There are three rules that should be strictly followed when using or interacting
	/// with JoinableTasks:
	///  1. If a method has certain thread apartment requirements (STA or MTA) it must either:
	///       a) Have an asynchronous signature, and asynchronously marshal to the appropriate 
	///          thread if it isn't originally invoked on a compatible thread. 
	///          The recommended way to switch to the main thread is:
	///          <code>
	///          await JoinableTaskFactory.SwitchToMainThreadAsync();
	///          </code>
	///       b) Have a synchronous signature, and throw an exception when called on the wrong thread.
	///     In particular, no method is allowed to synchronously marshal work to another thread
	///     (blocking while that work is done). Synchronous blocks in general are to be avoided
	///     whenever possible.
	///  2. When an implementation of an already-shipped public API must call asynchronous code
	///     and block for its completion, it must do so by following this simple pattern:
	///     <code>
	///     JoinableTaskFactory.Run(async delegate {
	///         await SomeOperationAsync(...);
	///     });
	///     </code>
	///  3. If ever awaiting work that was started earlier, that work must be Joined.
	///     For example, one service kicks off some asynchronous work that may later become
	///     synchronously blocking:
	///     <code>
	///     JoinableTask longRunningAsyncWork = JoinableTaskFactory.RunAsync(async delegate {
	///         await SomeOperationAsync(...);
	///     });
	///     </code>
	///     Then later that async work becomes blocking:
	///     <code>
	///     longRunningAsyncWork.Join();
	///     </code>
	///     or perhaps:
	///     <code>
	///     await longRunningAsyncWork;
	///     </code>
	///     Note however that this extra step is not necessary when awaiting is done
	///     immediately after kicking off an asynchronous operation.
	/// </remarks>
	public partial class JoinableTaskContext : IDisposable {
		/// <summary>
		/// A "global" lock that allows the graph of interconnected sync context and JoinableSet instances
		/// communicate in a thread-safe way without fear of deadlocks due to each taking their own private
		/// lock and then calling others, thus leading to deadlocks from lock ordering issues.
		/// </summary>
		/// <remarks>
		/// Yes, global locks should be avoided wherever possible. However even MEF from the .NET Framework
		/// uses a global lock around critical composition operations because containers can be interconnected
		/// in arbitrary ways. The code in this file has a very similar problem, so we use a similar solution.
		/// Except that our lock is only as global as the JoinableTaskContext. It isn't static.
		/// </remarks>
		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		private readonly object syncContextLock = new object();

		/// <summary>
		/// An AsyncLocal value that carries the joinable instance associated with an async operation.
		/// </summary>
		private readonly AsyncLocal<JoinableTask> joinableOperation = new AsyncLocal<JoinableTask>();

		/// <summary>
		/// The set of tasks that have started but have not yet completed.
		/// </summary>
		/// <remarks>
		/// All access to this collection should be guarded by locking this collection.
		/// </remarks>
		private readonly HashSet<JoinableTask> pendingTasks = new HashSet<JoinableTask>();

		/// <summary>
		/// A set of receivers of hang notifications.
		/// </summary>
		/// <remarks>
		/// All access to this collection should be guarded by locking this collection.
		/// </remarks>
		private readonly HashSet<JoinableTaskContextNode> hangNotifications = new HashSet<JoinableTaskContextNode>();

		/// <summary>
		/// A single joinable task factory that itself cannot be joined.
		/// </summary>
		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		private JoinableTaskFactory nonJoinableFactory;

		/// <summary>
		/// Initializes a new instance of the <see cref="JoinableTaskContext"/> class.
		/// </summary>
		/// <param name="mainThread">The thread to switch to in <see cref="JoinableTaskFactory.SwitchToMainThreadAsync(CancellationToken)"/>.</param>
		/// <param name="synchronizationContext">The synchronization context to use to switch to the main thread.</param>
		public JoinableTaskContext(Thread mainThread = null, SynchronizationContext synchronizationContext = null) {
			this.MainThread = mainThread ?? Thread.CurrentThread;
			this.UnderlyingSynchronizationContext = synchronizationContext ?? SynchronizationContext.Current; // may still be null after this.
		}

		/// <summary>
		/// Gets the factory which creates joinable tasks
		/// that do not belong to a joinable task collection.
		/// </summary>
		public JoinableTaskFactory Factory {
			get {
				using (NoMessagePumpSyncContext.Default.Apply()) {
					lock (this.SyncContextLock) {
						if (this.nonJoinableFactory == null) {
							this.nonJoinableFactory = this.CreateDefaultFactory();
						}

						return this.nonJoinableFactory;
					}
				}
			}
		}

		/// <summary>
		/// Gets the main thread that can be shared by tasks created by this context.
		/// </summary>
		public Thread MainThread { get; private set; }

		/// <summary>
		/// Gets a value indicating whether the caller is currently running within the context of a joinable task.
		/// </summary>
		/// <remarks>
		/// Use of this property is generally discouraged, as any operation that becomes a no-op when no
		/// ambient JoinableTask is present is very cheap. For clients that have complex algorithms that are
		/// only relevant if an ambient joinable task is present, this property may serve to skip that for
		/// performance reasons.
		/// </remarks>
		public bool IsWithinJoinableTask {
			get { return this.AmbientTask != null; }
		}

		/// <summary>
		/// Gets the underlying <see cref="SynchronizationContext"/> that controls the main thread in the host.
		/// </summary>
		internal SynchronizationContext UnderlyingSynchronizationContext { get; private set; }

		/// <summary>
		/// Gets the context-wide synchronization lock.
		/// </summary>
		internal object SyncContextLock {
			get { return this.syncContextLock; }
		}

		/// <summary>
		/// Gets the caller's ambient joinable task.
		/// </summary>
		internal JoinableTask AmbientTask {
			get { return this.joinableOperation.Value; }
			set { this.joinableOperation.Value = value; }
		}

		/// <summary>
		/// Conceals any JoinableTask the caller is associated with until the returned value is disposed.
		/// </summary>
		/// <returns>A value to dispose of to restore visibility into the caller's associated JoinableTask, if any.</returns>
		/// <remarks>
		/// <para>In some cases asynchronous work may be spun off inside a delegate supplied to Run,
		/// so that the work does not have privileges to re-enter the Main thread until the
		/// <see cref="JoinableTaskFactory.Run(Func{Task})"/> call has returned and the UI thread is idle.
		/// To prevent the asynchronous work from automatically being allowed to re-enter the Main thread,
		/// wrap the code that calls the asynchronous task in a <c>using</c> block with a call to this method 
		/// as the expression.</para>
		/// <example>
		/// <code>
		/// this.JoinableTaskContext.RunSynchronously(async delegate {
		///     using(this.JoinableTaskContext.SuppressRelevance()) {
		///         var asyncOperation = Task.Run(async delegate {
		///             // Some background work.
		///             await this.JoinableTaskContext.SwitchToMainThreadAsync();
		///             // Some Main thread work, that cannot begin until the outer RunSynchronously call has returned.
		///         });
		///     }
		///     
		///     // Because the asyncOperation is not related to this Main thread work (it was suppressed),
		///     // the following await *would* deadlock if it were uncommented.
		///     ////await asyncOperation;
		/// });
		/// </code>
		/// </example>
		/// </remarks>
		public RevertRelevance SuppressRelevance() {
			return new RevertRelevance(this);
		}

		/// <summary>
		/// Gets a value indicating whether the main thread is blocked for the caller's completion.
		/// </summary>
		public bool IsMainThreadBlocked() {
			var ambientTask = this.AmbientTask;
			if (ambientTask != null) {
				using (NoMessagePumpSyncContext.Default.Apply()) {
					lock (this.SyncContextLock) {
						var allJoinedJobs = new HashSet<JoinableTask>();
						lock (this.pendingTasks) {	 // our read lock doesn't cover this collection
							foreach (var pendingTask in this.pendingTasks) {
								if ((pendingTask.State & JoinableTask.JoinableTaskFlags.SynchronouslyBlockingMainThread) == JoinableTask.JoinableTaskFlags.SynchronouslyBlockingMainThread) {
									// This task blocks the main thread. If it has joined the ambient task
									// directly or indirectly, then our ambient task is considered blocking
									// the main thread.
									pendingTask.AddSelfAndDescendentOrJoinedJobs(allJoinedJobs);
									if (allJoinedJobs.Contains(ambientTask)) {
										return true;
									}

									allJoinedJobs.Clear();
								}
							}
						}
					}
				}
			}

			return false;
		}

		/// <summary>
		/// Creates a joinable task factory that automatically adds all created tasks
		/// to a collection that can be jointly joined.
		/// </summary>
		/// <param name="collection">The collection that all tasks should be added to.</param>
		public virtual JoinableTaskFactory CreateFactory(JoinableTaskCollection collection) {
			Requires.NotNull(collection, "collection");
			return new JoinableTaskFactory(collection);
		}

		/// <summary>
		/// Creates a collection for in-flight joinable tasks.
		/// </summary>
		/// <returns>A new joinable task collection.</returns>
		public JoinableTaskCollection CreateCollection() {
			return new JoinableTaskCollection(this);
		}

		/// <inheritdoc/>
		public void Dispose() {
			this.Dispose(true);
			GC.SuppressFinalize(this);
		}

		/// <summary>
		/// Disposes managed and unmanaged resources held by this instance.
		/// </summary>
		/// <param name="disposing"><c>true</c> if <see cref="Dispose()"/> was called; <c>false</c> if the object is being finalized.</param>
		protected virtual void Dispose(bool disposing) {
		}

		/// <summary>
		/// Invoked when a hang is suspected to have occurred involving the main thread.
		/// </summary>
		/// <param name="hangDuration">The duration of the current hang.</param>
		/// <param name="notificationCount">The number of times this hang has been reported, including this one.</param>
		/// <param name="hangId">A random GUID that uniquely identifies this particular hang.</param>
		/// <remarks>
		/// A single hang occurrence may invoke this method multiple times, with increasing
		/// values in the <paramref name="hangDuration"/> parameter.
		/// </remarks>
		[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
		protected internal virtual void OnHangDetected(TimeSpan hangDuration, int notificationCount, Guid hangId) {
			List<JoinableTaskContextNode> listeners;
			lock (this.hangNotifications) {
				listeners = this.hangNotifications.ToList();
			}

			foreach (var listener in listeners) {
				try {
					listener.OnHangDetected(hangDuration, notificationCount, hangId);
				} catch (Exception ex) {
					// Report it in CHK, but don't throw. In a hang situation, we don't want the product
					// to fail for another reason, thus hiding the hang issue.
					Report.Fail("Exception thrown from OnHangDetected listener. {0}", ex);
				}
			}
		}

		/// <summary>
		/// Creates a factory without a <see cref="JoinableTaskCollection"/>.
		/// </summary>
		/// <remarks>
		/// Used for initializing the <see cref="Factory"/> property.
		/// </remarks>
		protected internal virtual JoinableTaskFactory CreateDefaultFactory() {
			return new JoinableTaskFactory(this);
		}

		/// <summary>
		/// Raised when a joinable task starts.
		/// </summary>
		/// <param name="task">The task that has started.</param>
		internal void OnJoinableTaskStarted(JoinableTask task) {
			Requires.NotNull(task, "task");

			lock (this.pendingTasks) {
				Assumes.True(this.pendingTasks.Add(task));
			}
		}

		/// <summary>
		/// Raised when a joinable task completes.
		/// </summary>
		/// <param name="task">The completing task.</param>
		internal void OnJoinableTaskCompleted(JoinableTask task) {
			Requires.NotNull(task, "task");

			lock (this.pendingTasks) {
				this.pendingTasks.Remove(task);
			}
		}

		/// <summary>
		/// Registers a node for notification when a hang is detected.
		/// </summary>
		/// <param name="node"></param>
		/// <returns></returns>
		internal IDisposable RegisterHangNotifications(JoinableTaskContextNode node) {
			Requires.NotNull(node, "node");
			lock (this.hangNotifications) {
				if (!this.hangNotifications.Add(node)) {
					Verify.FailOperation(Strings.JoinableTaskContextNodeAlreadyRegistered);
				}
			}

			return new HangNotificationRegistration(node);
		}

		/// <summary>
		/// A value whose disposal cancels hang registration.
		/// </summary>
		private class HangNotificationRegistration : IDisposable {
			/// <summary>
			/// The node to receive notifications. May be <c>null</c> if <see cref="Dispose"/> has already been called.
			/// </summary>
			private JoinableTaskContextNode node;

			/// <summary>
			/// Initializes a new instance of the <see cref="HangNotificationRegistration"/> class.
			/// </summary>
			internal HangNotificationRegistration(JoinableTaskContextNode node) {
				Requires.NotNull(node, "node");
				this.node = node;
			}

			/// <summary>
			/// Removes the node from hang notifications.
			/// </summary>
			public void Dispose() {
				var node = this.node;
				if (node != null) {
					lock (node.Context.hangNotifications) {
						Assumes.True(node.Context.hangNotifications.Remove(node));
					}

					this.node = null;
				}
			}
		}

		/// <summary>
		/// A structure that clears CallContext and SynchronizationContext async/thread statics and
		/// restores those values when this structure is disposed.
		/// </summary>
		public struct RevertRelevance : IDisposable {
			private readonly JoinableTaskContext pump;
			private SpecializedSyncContext temporarySyncContext;
			private JoinableTask oldJoinable;

			/// <summary>
			/// Initializes a new instance of the <see cref="RevertRelevance"/> struct.
			/// </summary>
			/// <param name="pump">The instance that created this value.</param>
			internal RevertRelevance(JoinableTaskContext pump) {
				Requires.NotNull(pump, "pump");
				this.pump = pump;

				this.oldJoinable = pump.AmbientTask;
				pump.AmbientTask = null;

				var jobSyncContext = SynchronizationContext.Current as JoinableTaskSynchronizationContext;
				if (jobSyncContext != null) {
					SynchronizationContext appliedSyncContext = null;
					if (jobSyncContext.MainThreadAffinitized) {
						appliedSyncContext = pump.UnderlyingSynchronizationContext;
					}

					this.temporarySyncContext = appliedSyncContext.Apply(); // Apply() extension method allows null receiver
				} else {
					this.temporarySyncContext = default(SpecializedSyncContext);
				}
			}

			/// <summary>
			/// Reverts the async local and thread static values to their original values.
			/// </summary>
			public void Dispose() {
				if (this.pump != null) {
					this.pump.AmbientTask = this.oldJoinable;
				}

				this.temporarySyncContext.Dispose();
			}
		}
	}
}
