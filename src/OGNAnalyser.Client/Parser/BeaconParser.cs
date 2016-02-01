﻿using OGNAnalyser.Client.Models;
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace OGNAnalyser.Client.Parser
{
    public static class BeaconParser
    {
        private static readonly Regex matcherAPRSBaseRegex = new Regex(@"(.+?)>APRS,(TCPIP\*,)?(q[A-U].),(.+?):|(\d{6})+h(\d{4}\.\d{2})(N|S).(\d{5}\.\d{2})(E|W).{0,2}((\d{3})|(\d{73}))?(\/[0-9]{3}\/)?A=(\d{6}).*?");
        private static readonly Regex matcherAircraftBodyRegex = new Regex(@"(?:(?:\s\!W)([0-9]{2})(?:\!))?(?:\sid)([a-fA-F0-9]{8})(?:\s)([+-][0-9]{3,5})(?:fpm\s)([+-][0-9]\.[0-9])(?:rot\s)([0-9]{1,3}.[0-9])(?:dB\s)([0-9]{1,3})(?:e\s)([\+\-]?[0-9]{1,4}\.[0-9])(?:kHz\sgps)([0-9]{1,3})(?:x)([0-9]{1,3})(?:\s*)");
        private static readonly Regex matcherReceiverBodyRegex = new Regex(@"(?: )(.)*");

        public static Beacon ParseBeacon(string receivedLine)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(receivedLine))
                    throw new BeaconParserException("Received line empty!", receivedLine);

                Beacon beacon;

                receivedLine = receivedLine.Trim();

                if (!receivedLine.StartsWith("#"))
                {
                    var aprsMatches = matcherAPRSBaseRegex.Matches(receivedLine);

                    if (aprsMatches.Count != 2)
                        throw new BeaconParserException("APRS Base matcher failed.", receivedLine);
                    
                    string beaconType = aprsMatches[0].Groups[3].Value;

                    switch (beaconType)
                    {
                        case "qAS":
                            beacon = new AircraftBeacon();
                            break;

                        case "qAC":
                            beacon = new ReceiverBeacon();
                            break;

                        default:
                            throw new BeaconParserException($"Unknown package type {beaconType}", receivedLine);
                    }

                    ((IBaseAPRSBeacon)beacon).parseAPRSBaseData(aprsMatches[0].Groups);
                    ((IGeographicPositionAndDateTime)beacon).parseCoords(aprsMatches[1].Groups);
                    ((ConcreteBeacon)beacon).parseOgnConcreteBeacon(receivedLine);
                }
                else
                {
                    var comment = new InformationalComment();
                    comment.InformationalData = receivedLine;
                    beacon = comment;
                }

                beacon.ParsedDateTime = DateTimeOffset.Now;
                return beacon;
            }
            catch(BeaconParserException e)
            {
                throw e;
            }
            catch(Exception e)
            {
                throw new BeaconParserException("Unknown exception while parsing beacon.", receivedLine, e);
            }
        }

        private static void parseAPRSBaseData(this IBaseAPRSBeacon beacon, GroupCollection aprsBaseHeaderMatchGroup)
        {
            beacon.BeaconSender = aprsBaseHeaderMatchGroup[1].Value;
            beacon.BeaconReceiver = aprsBaseHeaderMatchGroup[4].Value;
        }

        private static void parseCoords(this IGeographicPositionAndDateTime position, GroupCollection aprsBaseCoordsMatchGroup)
        {
            try
            {
                position.PositionLocalTime = DateTime.ParseExact($"{DateTime.Now:yyyyMMdd} {aprsBaseCoordsMatchGroup[5].Value}", "yyyyMMdd HHmmss", CultureInfo.InvariantCulture);

                // lat (5111.32N)
                position.PositionLatDegrees = (float)Math.Round(float.Parse(aprsBaseCoordsMatchGroup[6].Value, CultureInfo.InvariantCulture) / 100f, 4);
                if (aprsBaseCoordsMatchGroup[7].Value == "S")
                    position.PositionLatDegrees *= -1;

                // lon (00102.04W)
                position.PositionLonDegrees = (float)Math.Round(float.Parse(aprsBaseCoordsMatchGroup[8].Value, CultureInfo.InvariantCulture) / 100f, 4);
                if (aprsBaseCoordsMatchGroup[9].Value == "W")
                    position.PositionLonDegrees *= -1;

                // Altitude
                position.PositionAltitudeMeters = (int)Math.Round(int.Parse(aprsBaseCoordsMatchGroup[14].Value, CultureInfo.InvariantCulture) / 3.28084f);
            }
            catch (Exception e)
            {
                throw new BeaconParserException("Error while parsing geographical position.", aprsBaseCoordsMatchGroup[0].Value, e);
            }
        }

        public static void parseOgnConcreteBeacon(this ConcreteBeacon beacon, string receivedLine)
        {
            switch(beacon.BeaconType)
            {
                case BeaconType.Aircraft:
                    ((AircraftBeacon)beacon).parseOgnAircraftData(receivedLine);
                    return;

                case BeaconType.Receiver:
                    ((ReceiverBeacon)beacon).parseOgnReceiverData(receivedLine);
                    return;
            }

            throw new BeaconParserException("Unknown beacon type for concrete beacon parser", receivedLine);
        }

        private static void parseOgnAircraftData(this AircraftBeacon beacon, string receivedLine)
        {
            try
            {
                var match = matcherAircraftBodyRegex.Match(receivedLine);

                // coords extension
                string coordsExt = match.Groups[1].Value.Trim();
                if(!string.IsNullOrWhiteSpace(coordsExt))
                {
                    beacon.PositionLatDegrees += float.Parse(coordsExt[0].ToString()) / 100000f;
                    beacon.PositionLonDegrees += float.Parse(coordsExt[1].ToString()) / 100000f;
                }

                // aircraft id
                beacon.AircraftId = ulong.Parse(match.Groups[2].Value.Trim(), NumberStyles.HexNumber);

                // climb rate
                beacon.ClimbRateMetersPerSecond = (float) Math.Round(float.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) * 0.00508f, 2);

                // rotation
                beacon.RotationRateHalfTurnPerTwoMins = (float) Math.Round(float.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture), 1);

                // SNR dB
                beacon.SignalNoiseRatioDb = (float)Math.Round(float.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture), 1);

                // forward correct tx errors
                beacon.TransmissionErrorsCorrected = int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture);

                // center frequency offset (kHz)
                beacon.CenterFrequencyOffsetKhz = (float)Math.Round(float.Parse(match.Groups[7].Value, CultureInfo.InvariantCulture), 1);

                // gps visible/channels
                beacon.GpsSatellitesVisible = int.Parse(match.Groups[8].Value, CultureInfo.InvariantCulture);
                beacon.GpsSatelliteChannelsAvailable = int.Parse(match.Groups[9].Value, CultureInfo.InvariantCulture); 
            }
            catch (Exception e)
            {
                throw new BeaconParserException("Error while parsing system info from aircraft beacon packet.", receivedLine, e);
            }
        }

        private static void parseOgnReceiverData(this ReceiverBeacon beacon, string receivedLine)
        {
            beacon.SystemInfo = matcherReceiverBodyRegex.Matches(receivedLine)[0].Value.Trim();
        }

        
        private static void parseReceiverBeaconBody(ReceiverBeacon beacon, string body)
        {
            try
            {
                //beacon.SystemInfo = body.Substring(body.IndexOf(' ') + 1);
            }
            catch (Exception e)
            {
                throw new BeaconParserException("Error while parsing system info from receiver beacon packet.", body, e);
            }
        }
    }
}