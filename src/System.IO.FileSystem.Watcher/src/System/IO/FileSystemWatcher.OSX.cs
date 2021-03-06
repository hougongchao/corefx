// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

using CFStringRef = System.IntPtr;
using FSEventStreamRef = System.IntPtr;
using size_t = System.IntPtr;
using FSEventStreamEventId = System.UInt64;
using CFRunLoopRef = System.IntPtr;
using Microsoft.Win32.SafeHandles;

namespace System.IO
{
    public partial class FileSystemWatcher
    {
        /// <summary>Called when FileSystemWatcher is finalized.</summary>
        private void FinalizeDispose()
        {
            // Make sure we cleanup
            StopRaisingEvents();
        }

        private void StartRaisingEvents()
        {
            // Don't start another instance if one is already runnings
            if (_cancellation != null)
            {
                return;
            }

            try
            {
                CancellationTokenSource cancellation = new CancellationTokenSource();
                RunningInstance instance = new RunningInstance(this, _directory, _includeSubdirectories, TranslateFlags(_notifyFilters), cancellation.Token);
                _enabled = true;
                _cancellation = cancellation;
                instance.Start();
            }
            catch
            {
                _enabled = false;
                _cancellation = null;
                throw;
            }
        }

        private void StopRaisingEvents()
        {
            _enabled = false;

            CancellationTokenSource token = _cancellation;
            if (token != null)
            {
                _cancellation = null;
                token.Cancel();
            }
        }

        // -----------------------------
        // ---- PAL layer ends here ----
        // -----------------------------

        private CancellationTokenSource _cancellation;

        private static Interop.EventStream.FSEventStreamEventFlags TranslateFlags(NotifyFilters flagsToTranslate)
        {
            Interop.EventStream.FSEventStreamEventFlags flags = 0;

            // Always re-create the filter flags when start is called since they could have changed
            if ((flagsToTranslate & (NotifyFilters.Attributes | NotifyFilters.CreationTime | NotifyFilters.LastAccess | NotifyFilters.LastWrite | NotifyFilters.Size)) != 0)
            {
                flags = Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemInodeMetaMod  | 
                               Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemFinderInfoMod |
                               Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemModified      |
                               Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemChangeOwner;
            }
            if ((flagsToTranslate & NotifyFilters.Security) != 0)
            {
                flags |= Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemChangeOwner | Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemXattrMod;
            }
            if ((flagsToTranslate & NotifyFilters.DirectoryName) != 0)
            {
                flags |= Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemIsDir |
                                Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemIsSymlink |
                                Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemCreated |
                                Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemRemoved |
                                Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemRenamed;
            }
            if ((flagsToTranslate & NotifyFilters.FileName) != 0)
            {
                flags |= Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemIsFile |
                                Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemIsSymlink |
                                Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemCreated |
                                Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemRemoved |
                                Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemRenamed;
            }

            return flags;
        }

        private sealed class RunningInstance
        {
            // Flags used to create the event stream
            private const Interop.EventStream.FSEventStreamCreateFlags EventStreamFlags = (Interop.EventStream.FSEventStreamCreateFlags.kFSEventStreamCreateFlagFileEvents | 
                                                                                        Interop.EventStream.FSEventStreamCreateFlags.kFSEventStreamCreateFlagNoDefer   | 
                                                                                        Interop.EventStream.FSEventStreamCreateFlags.kFSEventStreamCreateFlagWatchRoot);

            // Weak reference to the associated watcher. A weak reference is used so that the FileSystemWatcher may be collected and finalized,
            // causing an active operation to be torn down.
            private readonly WeakReference<FileSystemWatcher> _weakWatcher;

            // The user can input relative paths, which can muck with our path comparisons. Save off the 
            // actual full path so we can use it for comparing
            private string _fullDirectory;

            // Boolean if we allow events from nested folders
            private bool _includeChildren;

            // The bitmask of events that we want to send to the user
            private Interop.EventStream.FSEventStreamEventFlags _filterFlags;

            // The EventStream to listen for events on
            private SafeEventStreamHandle _eventStream;

            // A reference to the RunLoop that we can use to start or stop a Watcher
            private CFRunLoopRef _watcherRunLoop;

            // Callback delegate for the EventStream events 
            private Interop.EventStream.FSEventStreamCallback _callback;

            // Token to monitor for cancellation requests, upon which processing is stopped and all
            // state is cleaned up.
            private readonly CancellationToken _cancellationToken;

            // Calling RunLoopStop multiple times SegFaults so protect the call to it
            private bool _stopping;

            internal RunningInstance(
                FileSystemWatcher watcher,
                string directory,
                bool includeChildren,
                Interop.EventStream.FSEventStreamEventFlags filter,
                CancellationToken cancelToken)
            {
                Debug.Assert(string.IsNullOrEmpty(directory) == false);
                Debug.Assert(!cancelToken.IsCancellationRequested);

                _weakWatcher = new WeakReference<FileSystemWatcher>(watcher);
                _fullDirectory = System.IO.Path.GetFullPath(directory);
                _includeChildren = includeChildren;
                _filterFlags = filter;
                _cancellationToken = cancelToken;
                _cancellationToken.Register(obj => ((RunningInstance)obj).CancellationCallback(), this);
                _stopping = false;
            }

            private void CancellationCallback()
            {
                if (!_stopping)
                {
                    _stopping = true;

                    // Stop the FS event message pump
                    Interop.RunLoop.CFRunLoopStop(_watcherRunLoop);
                }
            }

            internal void Start()
            {
                // Make sure _fullPath doesn't contain a link or alias
                // since the OS will give back the actual, non link'd or alias'd paths
                _fullDirectory = Interop.Sys.RealPath(_fullDirectory);
                if (_fullDirectory == null)
                {
                    throw Interop.GetExceptionForIoErrno(Interop.Sys.GetLastErrorInfo(), _fullDirectory, true);
                }

                Debug.Assert(string.IsNullOrEmpty(_fullDirectory) == false, "Watch directory is null or empty");

                // Normalize the _fullDirectory path to have a trailing slash
                if (_fullDirectory[_fullDirectory.Length - 1] != '/')
                {
                    _fullDirectory += "/";
                }

                // Get the path to watch and verify we created the CFStringRef
                SafeCreateHandle path = Interop.CoreFoundation.CFStringCreateWithCString(_fullDirectory);
                if (path.IsInvalid)
                {
                    throw Interop.GetExceptionForIoErrno(Interop.Sys.GetLastErrorInfo(), _fullDirectory, true);
                }

                // Take the CFStringRef and put it into an array to pass to the EventStream
                SafeCreateHandle arrPaths = Interop.CoreFoundation.CFArrayCreate(new CFStringRef[1] { path.DangerousGetHandle() }, 1);
                if (arrPaths.IsInvalid)
                {
                    path.Dispose();
                    throw Interop.GetExceptionForIoErrno(Interop.Sys.GetLastErrorInfo(), _fullDirectory, true);
                }

                // Create the callback for the EventStream if it wasn't previously created for this instance.
                if (_callback == null)
                {
                    _callback = new Interop.EventStream.FSEventStreamCallback(FileSystemEventCallback);
                }

                // Make sure the OS file buffer(s) are fully flushed so we don't get events from cached I/O
                Interop.Sys.Sync();

                // Create the event stream for the path and tell the stream to watch for file system events.
                _eventStream = Interop.EventStream.FSEventStreamCreate(
                    _callback,
                    arrPaths,
                    Interop.EventStream.kFSEventStreamEventIdSinceNow,
                    0.0f,
                    EventStreamFlags);
                if (_eventStream.IsInvalid)
                {
                    arrPaths.Dispose();
                    path.Dispose();
                    throw Interop.GetExceptionForIoErrno(Interop.Sys.GetLastErrorInfo(), _fullDirectory, true);
                }

                // Create and start our watcher thread then wait for the thread to initialize and start 
                // the RunLoop. We wait for that to prevent this function from returning before the RunLoop
                // has a chance to start so that any callers won't race with the background thread's initialization
                // and calling Stop, which would attempt to stop a RunLoop that hasn't started yet.
                var runLoopStarted = new ManualResetEventSlim();
                new Thread(WatchForFileSystemEventsThreadStart) { IsBackground = true }.Start(runLoopStarted);
                runLoopStarted.Wait();
            }

            private void WatchForFileSystemEventsThreadStart(object arg)
            {
                var runLoopStarted = (ManualResetEventSlim)arg;

                // Get this thread's RunLoop
                _watcherRunLoop = Interop.RunLoop.CFRunLoopGetCurrent();
                Debug.Assert(_watcherRunLoop != IntPtr.Zero);

                // Schedule the EventStream to run on the thread's RunLoop
                Interop.EventStream.FSEventStreamScheduleWithRunLoop(_eventStream, _watcherRunLoop, Interop.RunLoop.kCFRunLoopDefaultMode);

                try
                {
                    bool started = Interop.EventStream.FSEventStreamStart(_eventStream);

                    // Notify the StartRaisingEvents call that we are initialized and about to start
                    // so that it can return and avoid a race-condition around multiple threads calling Stop and Start
                    runLoopStarted.Set();

                    if (started)
                    {
                        // Start the OS X RunLoop (a blocking call) that will pump file system changes into the callback function
                        Interop.RunLoop.CFRunLoopRun();

                        // When we get here, we've requested to stop so cleanup the EventStream and unschedule from the RunLoop
                        Interop.EventStream.FSEventStreamStop(_eventStream);
                    }
                    else
                    {
                        // Try to get the Watcher to raise the error event; if we can't do that, just silently exist since the watcher is gone anyway
                        FileSystemWatcher watcher;
                        if (_weakWatcher.TryGetTarget(out watcher))
                        {
                            // An error occurred while trying to start the run loop so fail out
                            watcher.OnError(new ErrorEventArgs(new IOException(SR.EventStream_FailedToStart, Marshal.GetLastWin32Error())));
                        }
                    }
                }
                finally
                {
                    // Always unschedule the RunLoop before cleaning up
                    Interop.EventStream.FSEventStreamUnscheduleFromRunLoop(_eventStream, _watcherRunLoop, Interop.RunLoop.kCFRunLoopDefaultMode);
                }
            }

            private void FileSystemEventCallback( 
                FSEventStreamRef streamRef, 
                IntPtr clientCallBackInfo, 
                size_t numEvents, 
                String[] eventPaths, 
                [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)]
                Interop.EventStream.FSEventStreamEventFlags[] eventFlags,
                [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)]
                FSEventStreamEventId[] eventIds)
            {
                Debug.Assert((numEvents.ToInt32() == eventPaths.Length) && (numEvents.ToInt32() == eventFlags.Length) && (numEvents.ToInt32() == eventIds.Length));

                // Try to get the actual watcher from our weak reference.  We maintain a weak reference most of the time
                // so as to avoid a rooted cycle that would prevent our processing loop from ever ending
                // if the watcher is dropped by the user without being disposed. If we can't get the watcher,
                // there's nothing more to do (we can't raise events), so bail.
                FileSystemWatcher watcher;
                if (!_weakWatcher.TryGetTarget(out watcher))
                {
                    CancellationCallback();
                    return;
                }

                // Since renames come in pairs, when we find the first we need to search for the next one. Once we find it, we'll add it to this
                // list so when the for-loop comes across it, we'll skip it since it's already been processed as part of the original of the pair.
                List<FSEventStreamEventId> handledRenameEvents = null;

                for (long i = 0; i < numEvents.ToInt32(); i++)
                {
                    Debug.Assert(eventPaths[i].Length > 0, "Empty events are not supported");
                    Debug.Assert(eventPaths[i][eventPaths[i].Length - 1] != '/', "Trailing slashes on events is not supported");

                    // Match Windows and don't notify us about changes to the Root folder
                    string path = eventPaths[i];
                    if (string.Compare(path, 0, _fullDirectory, 0, path.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        continue;
                    }

                    // First, we should check if this event should kick off a re-scan since we can't really rely on anything after this point if that is true
                    if (ShouldRescanOccur(eventFlags[i]))
                    {
                        watcher.OnError(new ErrorEventArgs(new IOException(SR.FSW_BufferOverflow, (int)eventFlags[i])));
                        break;
                    }
                    else if ((handledRenameEvents != null) && (handledRenameEvents.Contains(eventIds[i])))
                    {
                        // If this event is the second in a rename pair then skip it
                        continue;
                    }
                    else if ((CheckIfPathIsNested(path)) && ((_filterFlags & eventFlags[i]) != 0))
                    {
                        // The base FileSystemWatcher does a match check against the relative path before combining with 
                        // the root dir; however, null is special cased to signify the root dir, so check if we should use that.
                        string relativePath = null;
                        if (eventPaths[i].Equals(_fullDirectory, StringComparison.OrdinalIgnoreCase) == false)
                        {
                            // Remove the root directory to get the relative path
                            relativePath = eventPaths[i].Remove(0, _fullDirectory.Length);
                        }

                        // Check if this is a rename
                        if (IsFlagSet(eventFlags[i], Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemRenamed))
                        {
                            // Find the rename that is paired to this rename, which should be the next rename in the list
                            long pairedId = FindRenameChangePairedChange(i, eventPaths, eventFlags, eventIds);
                            if (pairedId == long.MinValue)
                            {
                                // Getting here means we have a rename without a pair, meaning it should be a create for the 
                                // move from unwatched folder to watcher folder scenario or a move from the watcher folder out.
                                // Check if the item exists on disk to check which it is
                                WatcherChangeTypes wct = DoesItemExist(eventPaths[i], IsFlagSet(eventFlags[i], Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemIsFile)) ?
                                    WatcherChangeTypes.Created : 
                                    WatcherChangeTypes.Deleted;
                                watcher.NotifyFileSystemEventArgs(wct, relativePath);
                            }
                            else
                            {
                                // Remove the base directory prefix and add the paired event to the list of 
                                // events to skip and notify the user of the rename 
                                string newPathRelativeName = eventPaths[pairedId].Remove(0, _fullDirectory.Length);
                                watcher.NotifyRenameEventArgs(WatcherChangeTypes.Renamed, newPathRelativeName, relativePath);

                                // Create a new list, if necessary, and add the event
                                if (handledRenameEvents == null)
                                {
                                    handledRenameEvents = new List<FSEventStreamEventId>();
                                }
                                handledRenameEvents.Add(eventIds[pairedId]);
                            }
                        }
                        else
                        {
                            // OS X is wonky where it can give back kFSEventStreamEventFlagItemCreated and kFSEventStreamEventFlagItemRemoved
                            // for the same item. The only option we have is to stat and see if the item exists; if so send created, otherwise send deleted.
                            if ((IsFlagSet(eventFlags[i], Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemCreated)) ||
                                (IsFlagSet(eventFlags[i], Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemRemoved)))
                            {
                                WatcherChangeTypes wct = DoesItemExist(eventPaths[i], IsFlagSet(eventFlags[i], Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemIsFile)) ?
                                    WatcherChangeTypes.Created : 
                                    WatcherChangeTypes.Deleted;
                                watcher.NotifyFileSystemEventArgs(wct, relativePath);
                            }

                            if (IsFlagSet(eventFlags[i], Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemInodeMetaMod) ||
                            IsFlagSet(eventFlags[i], Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemModified) ||
                            IsFlagSet(eventFlags[i], Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemFinderInfoMod) ||
                            IsFlagSet(eventFlags[i], Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemChangeOwner) ||
                            IsFlagSet(eventFlags[i], Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemXattrMod))
                            {
                                // Everything else is a modification
                                watcher.NotifyFileSystemEventArgs(WatcherChangeTypes.Changed, relativePath);
                            }
                        }
                    }
                }
            }

            private bool ShouldRescanOccur(Interop.EventStream.FSEventStreamEventFlags flags)
            {
                // Check if any bit is set that signals that the caller should rescan
                return (IsFlagSet(flags, Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagMustScanSubDirs) ||
                        IsFlagSet(flags, Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagUserDropped)     ||
                        IsFlagSet(flags, Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagKernelDropped)   ||
                        IsFlagSet(flags, Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagRootChanged)     ||
                        IsFlagSet(flags, Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagMount)           ||
                        IsFlagSet(flags, Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagUnmount));
            }

            private bool CheckIfPathIsNested(string eventPath)
            {
                bool doesPathPass = true;

                // If we shouldn't include subdirectories, check if this path's parent is the watch directory
                if (_includeChildren == false)
                {
                    // Check if the parent is the root. If so, then we'll continue processing based on the name.
                    // If it isn't, then this will be set to false and we'll skip the name processing since it's irrelevant.
                    string parent = System.IO.Path.GetDirectoryName(eventPath);
                    doesPathPass = (string.Compare(parent, 0, _fullDirectory, 0, parent.Length, StringComparison.OrdinalIgnoreCase) == 0);
                }

                return doesPathPass;
            }

            private long FindRenameChangePairedChange(
                long currentIndex, 
                String[] eventPaths,
                Interop.EventStream.FSEventStreamEventFlags[] eventFlags,
                FSEventStreamEventId[] eventIds)
            {
                // Start at one past the current index and try to find the next Rename item, which should be the old path.
                for (long i = currentIndex + 1; i < eventPaths.Length; i++)
                {
                    if (IsFlagSet(eventFlags[i], Interop.EventStream.FSEventStreamEventFlags.kFSEventStreamEventFlagItemRenamed))
                    {
                        // We found match, stop looking
                        return i;
                    }
                }

                return long.MinValue;
            }

            private static bool IsFlagSet(Interop.EventStream.FSEventStreamEventFlags flags, Interop.EventStream.FSEventStreamEventFlags value)
            {
                return (value & flags) == value;
            }

            private static bool DoesItemExist(string path, bool isFile)
            {
                if (isFile)
                    return File.Exists(path);
                else
                    return Directory.Exists(path);
            }
        }
    }
}
