using System;
using BeatSaber.Settings;
using Unity.Mathematics;

namespace RoomCalibration.Tests
{
    internal static class CoreTests
    {
        static int assertions;
        static void Assert(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new Exception(message);
        }
        static void Near(float actual, float expected, string name)
        {
            Assert(Math.Abs(actual - expected) < 0.0001f, name + ": " + actual + " != " + expected);
        }
        static void RoomNear(RoomSettings a, RoomSettings b, string name)
        {
            Near(a.center.x,b.center.x,name+".x"); Near(a.center.y,b.center.y,name+".y");
            Near(a.center.z,b.center.z,name+".z"); Near(Geometry.Wrap(a.rotation-b.rotation),0,name+".rotation");
        }
        static RoomSettings Room(float x, float y, float z, float rotation)
        {
            return new RoomSettings { center=new float3(x,y,z), rotation=rotation };
        }
        static float4 Yaw(float degrees)
        {
            double angle=degrees*Math.PI/360.0;
            return new float4(0,(float)Math.Sin(angle),0,(float)Math.Cos(angle));
        }
        static RoomSettings Calibrate(RoomSettings room, float3 p, float4 q, float target)
        {
            RoomSettings result; string error;
            Assert(Geometry.TryCalibrate(room,p,q,target,out result,out error),"calibration failed: "+error);
            return result;
        }
        static void GeometryCases()
        {
            RoomSettings original=Room(1,0.25f,-1,37);
            RoomNear(Calibrate(original,new float3(2,1.7f,3),Yaw(0),0),Room(-2,0.25f,-3,0),"forward");
            RoomNear(Calibrate(original,new float3(2,1.7f,3),Yaw(90),0),Room(3,0.25f,-2,-90),"right");
            RoomNear(Calibrate(original,new float3(2,1.7f,3),Yaw(-90),0),Room(-3,0.25f,2,90),"left");
            RoomNear(Calibrate(original,new float3(2,1.7f,3),Yaw(180),0),Room(2,0.25f,3,-180),"back");
            RoomNear(Calibrate(original,new float3(6,1.7f,-5),Yaw(0),0),Room(-6,0.25f,5,0),"beyond native guard");
            RoomNear(Calibrate(original,new float3(2,1.7f,3),Yaw(0),90),Room(-3,0.25f,2,90),"rotated chart");
            float4 tilted=new float4(0.1830127f,0.6830127f,-0.1830127f,0.6830127f);
            RoomNear(Calibrate(original,new float3(2,1.7f,3),tilted,0),Room(3,0.25f,-2,-90),"pitch ignored");
            float4 rolled=new float4(0.1830127f,0.6830127f,0.1830127f,0.6830127f);
            RoomNear(Calibrate(original,new float3(2,1.7f,3),rolled,0),Room(3,0.25f,-2,-90),"roll ignored");
            RoomSettings result; string error;
            Assert(!Geometry.TryCalibrate(original,new float3(Single.NaN,0,0),Yaw(0),0,out result,out error),"reject NaN");
            Assert(!Geometry.TryCalibrate(original,new float3(),new float4(),0,out result,out error),"reject zero quaternion");
            Assert(!Geometry.TryCalibrate(original,new float3(),new float4(0.70710678f,0,0,0.70710678f),0,out result,out error),"reject vertical gaze");
            RoomNear(result,original,"failed calibration is unchanged");
            RoomNear(Geometry.Compose(original,Geometry.Inverse(original)),Room(0,0,0,0),"transform inverse");
        }
        static void SessionCases()
        {
            SessionTransform session=new SessionTransform();
            RoomSettings basis=Room(1,0.2f,-2,25);
            RoomSettings effective=Calibrate(basis,new float3(3,1.6f,4),Yaw(80),0);
            session.Observe(basis); session.Commit(effective,basis);
            Assert(session.Active,"active session");
            RoomNear(session.PersistentRoom(effective),basis,"save omits calibration");
            SessionSnapshot first=session.Snapshot;
            RoomSettings second=Calibrate(effective,new float3(3,1.6f,4),Yaw(80),0);
            session.Commit(second,effective);
            RoomNear(second,effective,"repeated calibration stable");
            RoomNear(session.PersistentRoom(second),basis,"repeated save baseline stable");
            RoomSettings explicitBasis=Room(2,0.5f,-3,40);
            RoomSettings edited=Geometry.Compose(session.Snapshot.Correction,explicitBasis);
            session.Observe(edited);
            RoomNear(session.PersistentRoom(edited),explicitBasis,"native edit persists without session");
            session.OriginChanged();
            Assert(session.NeedsCalibration && session.Active,"origin change invalidates without moving room");
            session.Commit(effective,edited);
            Assert(!session.NeedsCalibration,"fresh calibration clears stale marker");
            RoomNear(session.Clear(effective),explicitBasis,"clear preserves explicit edits");
            Assert(!session.Active,"clear disables session");
            session.Restore(first);
            RoomNear(session.PersistentRoom(effective),basis,"rollback restores baseline");
            session.NativeReset();
            Assert(!session.Active,"native reset clears session");
            RoomNear(session.PersistentRoom(Room(0,0,0,0)),Room(0,0,0,0),"native reset persistence");
        }
        static void StartupCases()
        {
            StartupCalibrationGate gate = new StartupCalibrationGate();
            gate.Begin(false);
            Assert(!gate.Pending && !gate.BeforeFirstMenuFrame(), "default off does not request automatic calibration");
            Assert(gate.MenuPresented && !gate.BeforeFirstMenuFrame(), "disabled option still closes the launch window");
            gate.Begin(true);
            Assert(gate.Pending && !gate.MenuPresented, "enabled option waits for menu first render");
            Assert(gate.BeforeFirstMenuFrame(), "enabled option requests calibration before first render");
            Assert(!gate.Pending && gate.MenuPresented && !gate.BeforeFirstMenuFrame(), "render boundary consumes request even when tracking or apply fails");
            Assert(!gate.BeforeFirstMenuFrame(), "late tracking and repeated renders cannot move the visible menu");
            gate.Begin(true);
            gate.Complete();
            Assert(!gate.BeforeFirstMenuFrame(), "manual startup calibration prevents automatic override");
            SessionTransform session=new SessionTransform();
            RoomSettings basis=Room(1,0.2f,-2,25);
            RoomSettings effective=Calibrate(basis,new float3(3,1.6f,4),Yaw(80),0);
            session.Commit(effective,basis);
            RoomNear(session.Attach(basis),effective,"app context rebuild keeps the existing correction");
            RoomSettings persisted=session.PersistentRoom(effective);
            Assert(session.Active,"detaching a settings owner does not discard process calibration");
            RoomNear(persisted,basis,"detaching restores the previous owner's persistent room");
            RoomNear(session.Attach(persisted),effective,"replacement settings owner receives the same center without resampling head");
            RoomNear(session.PersistentRoom(effective),basis,"replacement settings owner still saves only baseline");
        }
        static void GestureCases()
        {
            ButtonChord gesture=new ButtonChord();
            Assert(!gesture.Tick(true,true,true),"startup hold cannot trigger");
            Assert(!gesture.Tick(true,true,true),"startup requires release");
            gesture.Tick(false,false,true);
            Assert(gesture.Tick(true,true,true),"chord starts preparation and pause immediately");
            Assert(!gesture.Tick(true,true,true),"held buttons do not start duplicate requests");
            gesture.Tick(false,true,true);
            Assert(!gesture.Tick(true,true,true),"partial release does not rearm");
            gesture.Tick(false,false,true);
            Assert(gesture.Tick(true,true,true),"full release permits next preparation");
            Assert(!gesture.Tick(true,true,false),"focus loss resets chord");
            Assert(!gesture.Tick(true,true,true),"return to focus requires release");
            gesture.Tick(false,false,true);
            Assert(gesture.Tick(true,true,true),"fresh chord works after focus recovery");
            Near(InputSchema.Duration(Single.NaN),1.5f,"invalid duration default");
            Near(InputSchema.Duration(0),0.5f,"duration lower bound");
            Near(InputSchema.Duration(30),5f,"duration upper bound");
        }
        static void RequestCases()
        {
            RequestGate gate=new RequestGate();
            Assert(gate.Begin(false,1,0),"begin request");
            Assert(!gate.Begin(true,1,0.2),"no queue or duplicate");
            Assert(gate.Tick(1,true,false,1)==RequestAction.None,"wait for pause");
            Assert(gate.Tick(1,true,true,1.1)==RequestAction.Apply,"confirmed pause permits apply");
            Assert(!gate.Pending,"apply consumes request");
            Assert(gate.Tick(1,true,true,1.2)==RequestAction.None,"no duplicate apply");
            gate.Begin(false,1,2);
            Assert(gate.Tick(1,true,false,4)==RequestAction.Timeout,"pause timeout");
            gate.Begin(false,1,5);
            Assert(gate.Tick(2,true,true,5.1)==RequestAction.Cancel,"scene changes reject stale request");
            gate.Begin(false,2,6);
            Assert(gate.Tick(2,false,true,6.1)==RequestAction.Cancel,"focus/tracking eligibility cancellation");
            gate.Begin(true,2,7); gate.Cancel();
            Assert(gate.Tick(2,true,true,7.1)==RequestAction.None,"disconnect cancels queued work");
            gate.Begin(false,3,10,1.5);
            Assert(gate.Tick(3,true,false,10.5)==RequestAction.None,"countdown waits for pause confirmation");
            Assert(gate.Tick(3,true,true,11)==RequestAction.Preparing,"countdown is visible before calibration");
            Assert(Math.Abs(gate.Remaining(11.5)-1)<0.0001,"countdown remaining duration");
            Assert(gate.Tick(3,true,true,12.49)==RequestAction.Preparing,"pose is not applied before countdown ends");
            Assert(gate.Tick(3,true,true,12.5)==RequestAction.Apply,"pose sampled after full preparation interval");
            gate.Begin(false,4,20,5);
            gate.Tick(4,true,true,20);
            Assert(gate.Tick(4,true,false,21)==RequestAction.Cancel,"resuming during countdown cancels calibration");
        }
        public static int Main()
        {
            try { GeometryCases(); SessionCases(); StartupCases(); GestureCases(); RequestCases();
                Console.WriteLine("PASS "+assertions+" assertions: geometry, persistence, gesture and request lifecycle"); return 0; }
            catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
