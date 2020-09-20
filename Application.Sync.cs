        private async Task ProcessRequestsAsync()
        {
            while (true)
            {
                var readResult = await Reader.ReadAsync(default);
                var buffer = readResult.Buffer;
                var isCompleted = readResult.IsCompleted;

                if (buffer.IsEmpty && isCompleted)
                {
                    return;
                }

                if (!HandleRequests(buffer, isCompleted))
                {
                    return;
                }

                await Writer.FlushAsync(default);
            }
        }

        private bool HandleRequests(in ReadOnlySequence<byte> buffer, bool isCompleted)
        {
            var reader = new SequenceReader<byte>(buffer);
            var writer = GetWriter(Writer, sizeHint: 160 * 16); // 160*16 is for Plaintext, for Json 160 would be enough

            while (true)
            {
                if (!ParseHttpRequest(ref reader, isCompleted))
                {
                    return false;
                }

                if (_state == State.Body)
                {
                    ProcessRequest(ref writer);

                    _state = State.StartLine;

                    if (!reader.End)
                    {
                        // More input data to parse
                        continue;
                    }
                }

                // No more input or incomplete data, Advance the Reader
                Reader.AdvanceTo(reader.Position, buffer.End);
                break;
            }

            writer.Commit();
            return true;
        }

        private bool ParseHttpRequest(ref SequenceReader<byte> reader, bool isCompleted)
        {
            var state = _state;

            if (state == State.StartLine)
            {
                if (Parser.ParseRequestLine(new ParsingAdapter(this), ref reader))
                {
                    state = State.Headers;
                }
            }

            if (state == State.Headers)
            {
                var success = Parser.ParseHeaders(new ParsingAdapter(this), ref reader);

                if (success)
                {
                    state = State.Body;
                }
            }

            if (state != State.Body && isCompleted)
            {
                ThrowUnexpectedEndOfData();
            }

            _state = state;
            return true;
        }
