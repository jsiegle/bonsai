﻿using System;
using System.ComponentModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace Bonsai.Arduino
{
    [DefaultProperty("Feature")]
    [Description("Receives a sequence of system exclusive messages from the specified Arduino.")]
    public class ReceiveSysex : Source<byte[]>
    {
        [TypeConverter(typeof(PortNameConverter))]
        [Description("The name of the serial port used to communicate with the Arduino.")]
        public string PortName { get; set; }

        [Description("The feature ID used to identify the system exclusive message payload.")]
        public int Feature { get; set; }

        public override IObservable<byte[]> Generate()
        {
            return Observable.Create<byte[]>(async observer =>
            {
                var connection = await ArduinoManager.ReserveConnectionAsync(PortName);
                EventHandler<SysexReceivedEventArgs> sysexReceived;
                sysexReceived = (sender, e) =>
                {
                    if (e.Feature == Feature)
                    {
                        observer.OnNext(e.Args);
                    }
                };

                connection.Arduino.SysexReceived += sysexReceived;
                return Disposable.Create(() =>
                {
                    connection.Arduino.SysexReceived -= sysexReceived;
                    connection.Dispose();
                });
            });
        }
    }
}
