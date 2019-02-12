// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;

namespace Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http
{
    public class HttpRequestPipeReader : PipeReader
    {
        private MessageBody _body;
        private HttpStreamState _state;
        private Exception _error;

        public HttpRequestPipeReader()
        {
            _state = HttpStreamState.Closed;
        }

        public override void AdvanceTo(SequencePosition consumed)
        {
            _body.AdvanceTo(consumed);
        }

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            _body.AdvanceTo(consumed, examined);
        }

        public override void CancelPendingRead()
        {
            _body.CancelPendingRead();
        }

        public override void Complete(Exception exception = null)
        {
            // TODO going to let this noop for now, I think we can support it but avoiding for now.
            //throw new NotImplementedException();
        }

        public override void OnWriterCompleted(Action<Exception, object> callback, object state)
        {
            _body.OnWriterCompleted(callback, state);
        }

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            ValidateState(cancellationToken);

            return _body.ReadAsync(cancellationToken);
        }

        public override bool TryRead(out ReadResult result)
        {
            // TODO validate state
            return _body.TryRead(out result);
        }

        public void StartAcceptingReads(MessageBody body)
        {
            // Only start if not aborted
            if (_state == HttpStreamState.Closed)
            {
                _state = HttpStreamState.Open;
                _body = body;
            }
        }

        public ValueTask<int> ReadAsyncForStream(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            ValidateState(cancellationToken);

            return _body.ReadAsync(buffer, cancellationToken);
        }

        public void StopAcceptingReads()
        {
            // Can't use dispose (or close) as can be disposed too early by user code
            // As exampled in EngineTests.ZeroContentLengthNotSetAutomaticallyForCertainStatusCodes
            _state = HttpStreamState.Closed;
            _body = null;
        }

        public void Abort(Exception error = null)
        {
            // We don't want to throw an ODE until the app func actually completes.
            // If the request is aborted, we throw a TaskCanceledException instead,
            // unless error is not null, in which case we throw it.
            if (_state != HttpStreamState.Closed)
            {
                _state = HttpStreamState.Aborted;
                _error = error;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ValidateState(CancellationToken cancellationToken)
        {
            var state = _state;
            if (state == HttpStreamState.Open)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            else if (state == HttpStreamState.Closed)
            {
                ThrowObjectDisposedException();
            }
            else
            {
                if (_error != null)
                {
                    ExceptionDispatchInfo.Capture(_error).Throw();
                }
                else
                {
                    ThrowTaskCanceledException();
                }
            }

            void ThrowObjectDisposedException() => throw new ObjectDisposedException(nameof(HttpRequestStream));
            void ThrowTaskCanceledException() => throw new TaskCanceledException();
        }

        public Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }
            if (bufferSize <= 0)
            {
                throw new ArgumentException(CoreStrings.PositiveNumberRequired, nameof(bufferSize));
            }

            ValidateState(cancellationToken);

            return CopyToAsyncInternal(destination, cancellationToken);
        }

        private async Task CopyToAsyncInternal(Stream destination, CancellationToken cancellationToken)
        {
            try
            {
                await _body.CopyToAsync(destination, cancellationToken);
            }
            catch (ConnectionAbortedException ex)
            {
                throw new TaskCanceledException("The request was aborted", ex);
            }
        }
    }
}