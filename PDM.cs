using System;
using System.Drawing;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using LemonUI.Menus;
using LemonUI.Scaleform;
using GtaScreen = GTA.UI.Screen;

namespace PremiumDeluxeRevamped
{
    public class PDM : Script
    {
        private static string Gxt(string key) => Game.GetLocalizedString(key);

        private static string LocalizedOrDefault(string key, string fallback)
        {
            string value = Gxt(key);
            return string.IsNullOrWhiteSpace(value) || string.Equals(value, "NULL", StringComparison.OrdinalIgnoreCase)
                ? fallback
                : value;
        }

        private const float PdmPedChairSearchRadius = 3.0f;
        private const float PdmPedSeatSnapDistance = 0.65f;
        private const int PdmPedSeatRetryIntervalMs = 1500;
        private static readonly Vector3 PdmChairSeatOffset = new Vector3(0.0f, -0.04f, 0.52f);
        private const float PdmDoorGreetingDistance = 4.75f;
        private const int PdmPedGreetingCooldownMs = 6000;
        private static int pdmPedNextSeatRetryAt;
        private static int pdmPedNextGreetingAt;
        private static bool clerkGreetedForCurrentVisit;

        public PDM()
        {
            try
            {
                Tick += PDM_OnTick;
                Aborted += OnAborted;

                Helper.LoadSettings();
                logger.Log("Premium Deluxe Motorsport initialized. Logging enabled.");
                Helper.BtnRotLeft = new InstructionalButton(LocalizedOrDefault("CMM_MOD_S6", "Doors"), Helper.keyDoor);
                Helper.BtnRotRight = new InstructionalButton(LocalizedOrDefault("CMOD_MOD_ROF", "Roof"), Helper.keyRoof);
                Helper.BtnCamera = new InstructionalButton(LocalizedOrDefault("CTRL_0", "Camera"), Helper.keyCamera);
                Helper.BtnZoom = new InstructionalButton(LocalizedOrDefault("HUD_INPUT91", "Zoom"), Helper.keyZoom);

                CreateEntrance();
                GlobalVariable.Get((int)Helper.GetGlobalValue()).Write(1);
                MenuHelper._menuPool = new LemonUI.ObjectPool();

                Helper.poly.Add(new Vector3(-71.54493f, -1060.757f, 27.5556f));
                Helper.poly.Add(new Vector3(-94.17564f, -1126.55f, 25.79746f));
                Helper.poly.Add(new Vector3(-17.57518f, -1125.392f, 27.11017f));
                Helper.poly.Add(new Vector3(-3.737129f, -1081.494f, 26.67219f));

                Helper.testDeivePoly.Add(new Vector3(-123.3222f, -1155.505f, 25.70785f));
                Helper.testDeivePoly.Add(new Vector3(76.87627f, -1143.797f, 29.22843f));
                Helper.testDeivePoly.Add(new Vector3(129.4713f, -989.3712f, 29.30896f));
                Helper.testDeivePoly.Add(new Vector3(-55.58704f, -921.9064f, 29.28478f));

                Function.Call(Hash.REQUEST_SCRIPT_AUDIO_BANK, "VEHICLE_SHOP_HUD_1", false, -1);
                Function.Call(Hash.REQUEST_SCRIPT_AUDIO_BANK, "VEHICLE_SHOP_HUD_2", false, -1);
                MenuHelper.CreateMenus();

                Helper.ToggleIPL("shr_int", "fakeint");
                Helper.LoadMissingProps();
            }
            catch (Exception ex)
            {
                logger.Log(ex.Message + " " + ex.StackTrace);
            }
        }

        public void CreateEntrance()
        {
            Helper.PdmDoor = new Vector3(-55.99228f, -1098.51f, 25.423f);
            Helper.PdmBlip = World.CreateBlip(Helper.PdmDoor);
            Helper.PdmBlip.Sprite = BlipSprite.SportsCar;
            Helper.PdmBlip.Color = BlipColor.RedLight;
            Helper.PdmBlip.IsShortRange = true;
        }

        private static Prop FindPdmChair()
        {
            Prop nearestChair = null;
            float nearestChairDistance = float.MaxValue;

            foreach (Prop prop in World.GetNearbyProps(Helper.PdmDoor, PdmPedChairSearchRadius, "v_corp_offchair"))
            {
                if (prop == null || !prop.Exists())
                {
                    continue;
                }

                prop.IsPositionFrozen = true;
                float chairDistance = prop.Position.DistanceToSquared(Helper.PdmDoor);
                if (chairDistance < nearestChairDistance)
                {
                    nearestChairDistance = chairDistance;
                    nearestChair = prop;
                }
            }

            return nearestChair;
        }

        private static Vector3 GetChairSeatPosition(Prop chair)
        {
            try
            {
                return Function.Call<Vector3>(Hash.GET_OFFSET_FROM_ENTITY_IN_WORLD_COORDS, chair, PdmChairSeatOffset.X, PdmChairSeatOffset.Y, PdmChairSeatOffset.Z);
            }
            catch
            {
                return new Vector3(chair.Position.X, chair.Position.Y, chair.Position.Z + PdmChairSeatOffset.Z);
            }
        }

        private static float GetChairSeatHeading(Prop chair)
        {
            float heading = chair.Heading + 180.0f;
            if (heading >= 360.0f)
            {
                heading -= 360.0f;
            }

            return heading;
        }

        private static void EnsurePdmPedSeated(Prop chair)
        {
            if (chair == null || !chair.Exists())
            {
                return;
            }

            Vector3 seatPosition = GetChairSeatPosition(chair);
            float seatHeading = GetChairSeatHeading(chair);
            int currentTime = Game.GameTime;
            bool createdPed = false;

            if (Helper.pdmPed == null || !Helper.pdmPed.Exists() || Helper.pdmPed.IsDead)
            {
                try { Helper.pdmPed?.Delete(); } catch { }
                Helper.pdmPed = World.CreatePed(PedHash.Hipster01AFY, seatPosition, seatHeading);
                if (Helper.pdmPed == null || !Helper.pdmPed.Exists())
                {
                    return;
                }

                Helper.pdmPed.IsPersistent = true;
                createdPed = true;
            }

            Helper.pdmPed.BlockPermanentEvents = true;
            Helper.pdmPed.KeepTaskWhenMarkedAsNoLongerNeeded = true;

            try { Helper.pdmPed.CanRagdoll = false; } catch { }
            try { Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, Helper.pdmPed, true); } catch { }

            bool isUsingSeatScenario = false;
            bool isRagdoll = false;
            bool isGettingIntoVehicle = false;
            float distanceToSeatSquared = Helper.pdmPed.Position.DistanceToSquared(seatPosition);

            try { isUsingSeatScenario = Function.Call<bool>(Hash.IS_PED_USING_SCENARIO, Helper.pdmPed, "PROP_HUMAN_SEAT_CHAIR_UPRIGHT"); } catch { }
            try { isRagdoll = Function.Call<bool>(Hash.IS_PED_RAGDOLL, Helper.pdmPed); } catch { }
            try { isGettingIntoVehicle = Function.Call<bool>(Hash.IS_PED_GETTING_INTO_A_VEHICLE, Helper.pdmPed); } catch { }

            bool forceImmediateSeat = createdPed || isRagdoll || distanceToSeatSquared > (PdmPedSeatSnapDistance * PdmPedSeatSnapDistance);
            bool canRetrySeatTask = currentTime >= pdmPedNextSeatRetryAt;
            bool shouldSeat = forceImmediateSeat || (!isUsingSeatScenario && !isGettingIntoVehicle && canRetrySeatTask);

            if (!shouldSeat)
            {
                return;
            }

            Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, Helper.pdmPed);
            Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, Helper.pdmPed, seatPosition.X, seatPosition.Y, seatPosition.Z, false, false, false);
            Helper.pdmPed.Heading = seatHeading;

            Function.Call(
                Hash.TASK_START_SCENARIO_AT_POSITION,
                Helper.pdmPed,
                "PROP_HUMAN_SEAT_CHAIR_UPRIGHT",
                seatPosition.X,
                seatPosition.Y,
                seatPosition.Z,
                seatHeading,
                -1,
                true,
                true);

            pdmPedNextSeatRetryAt = currentTime + PdmPedSeatRetryIntervalMs;
        }


        private static void ResetClerkGreetingState()
        {
            clerkGreetedForCurrentVisit = false;
        }

        private static void TryGreetPlayerFromClerk()
        {
            if (Helper.GPC == null || !Helper.GPC.Exists() || Helper.GPC.IsDead || Helper.GPC.IsInVehicle())
            {
                return;
            }

            if (Helper.pdmPed == null || !Helper.pdmPed.Exists() || Helper.pdmPed.IsDead)
            {
                return;
            }

            if (!Helper.poly.IsInInterior(Helper.GPC.Position))
            {
                ResetClerkGreetingState();
                return;
            }

            if (clerkGreetedForCurrentVisit || Game.GameTime < pdmPedNextGreetingAt)
            {
                return;
            }

            if (Helper.GPC.Position.DistanceToSquared(Helper.PdmDoor) > (PdmDoorGreetingDistance * PdmDoorGreetingDistance))
            {
                return;
            }

            try
            {
                Function.Call(Hash.PLAY_PED_AMBIENT_SPEECH_NATIVE, Helper.pdmPed, "GENERIC_HI", "SPEECH_PARAMS_FORCE_NORMAL_CLEAR");
                clerkGreetedForCurrentVisit = true;
                pdmPedNextGreetingAt = Game.GameTime + PdmPedGreetingCooldownMs;
            }
            catch (Exception ex)
            {
                logger.Log("Error Clerk Greeting " + ex.Message + " " + ex.StackTrace);
            }
        }

        public void PDM_OnTick(object o, EventArgs e)
        {
            try
            {
                Helper.GP = Game.Player;
                Helper.GPC = Game.Player.Character;
                switch ((PedHash)Helper.GPC.Model.Hash)
                {
                    case PedHash.Michael:
                    case PedHash.Franklin:
                    case PedHash.Trevor:
                        Helper.PlayerCash = Game.Player.Money;
                        break;
                    default:
                        Helper.PlayerCash = 1999999999;
                        break;
                }

                Helper.PdmDoorDist = World.GetDistance(Helper.GPC.Position, Helper.PdmDoor);

                try
                {
                    if (Helper.PdmDoorDist < 10.0f)
                    {
                        Prop chair = FindPdmChair();
                        EnsurePdmPedSeated(chair);
                    }
                }
                catch (Exception ex)
                {
                    try { Helper.pdmPed?.Delete(); } catch { }
                    pdmPedNextSeatRetryAt = 0;
                    logger.Log("Error Create Ped " + ex.Message + " " + ex.StackTrace);
                }

                if (Helper.poly.IsInInterior(Helper.GPC.Position))
                {
                    TryGreetPlayerFromClerk();
                }
                else
                {
                    ResetClerkGreetingState();
                }

                if (!Helper.GPC.IsInVehicle() && !Helper.GPC.IsDead && Helper.PdmDoorDist < 3.0f && Helper.GP.Wanted.WantedLevel == 0 && Helper.TaskScriptStatus == -1)
                {
                    Helper.DisplayHelpTextThisFrame(Gxt("SHR_MENU"));
                }
                else if (!Helper.GPC.IsInVehicle() && !Helper.GPC.IsDead && Helper.PdmDoorDist < 3.0f && Helper.GP.Wanted.WantedLevel >= 1)
                {
                    Function.Call(Hash.DISPLAY_HELP_TEXT_THIS_FRAME, "LOSE_WANTED", 0);
                }

                if (Helper.TestDrive == 3 && !Helper.GPC.IsInVehicle())
                {
                    GtaScreen.FadeOut(200);
                    Wait(200);
                    double penalty = Helper.VehiclePrice / 99d;
                    if (Helper.VehPreview.HasBeenDamagedBy(Helper.GPC))
                    {
                        Helper.GP.Money = Helper.PlayerCash - (Helper.VehiclePrice / 99);
                        GtaScreen.ShowSubtitle("$" + Math.Round(penalty).ToString("###,###") + Helper.GetLangEntry("HELP_PENALTY"));
                    }

                    MenuHelper.ShowOnly(MenuHelper.ConfirmMenu);
                    Helper.VehPreview.IsUndriveable = true;
                    Helper.VehPreview.LockStatus = VehicleLockStatus.IgnoredByPlayer;
                    Helper.VehPreview.Position = Helper.VehPreviewPos;
                    Helper.VehPreview.Heading = Helper.Radius;
                    Function.Call(Hash.SET_VEHICLE_DOORS_SHUT, Helper.VehPreview, false);
                    Function.Call(Hash.SET_VEHICLE_FIXED, Helper.VehPreview);
                    Helper.GPC.Position = Helper.PlayerLastPos;
                    Helper.TestDrive = 1;
                    Helper.HideHud = true;
                    Wait(200);
                    GtaScreen.FadeIn(200);
                    Helper.ShowVehicleName = true;
                    Helper.wsCamera.RepositionFor(Helper.VehPreview);
                }
                else if (Helper.TestDrive == 3 && !Helper.testDeivePoly.IsInInterior(Helper.GPC.Position))
                {
                    GtaScreen.FadeOut(200);
                    Wait(200);
                    double penalty = Helper.VehiclePrice / 99d;
                    if (Helper.VehPreview.HasBeenDamagedBy(Helper.GPC))
                    {
                        Helper.GP.Money = Helper.PlayerCash - (Helper.VehiclePrice / 99);
                        Notification.PostTicker("$" + Math.Round(penalty).ToString("###,###") + Helper.GetLangEntry("HELP_PENALTY"), false);
                    }

                    MenuHelper.ShowOnly(MenuHelper.ConfirmMenu);
                    Helper.VehPreview.IsUndriveable = true;
                    Helper.VehPreview.LockStatus = VehicleLockStatus.IgnoredByPlayer;
                    Helper.VehPreview.Position = Helper.VehPreviewPos;
                    Helper.VehPreview.Heading = Helper.Radius;
                    Function.Call(Hash.SET_VEHICLE_DOORS_SHUT, Helper.VehPreview, false);
                    Function.Call(Hash.SET_VEHICLE_FIXED, Helper.VehPreview);
                    Helper.GPC.Position = Helper.PlayerLastPos;
                    Helper.TestDrive = 1;
                    Helper.HideHud = true;
                    Wait(200);
                    GtaScreen.FadeIn(200);
                    Helper.ShowVehicleName = true;
                    Helper.wsCamera.RepositionFor(Helper.VehPreview);
                }
                else if (Helper.TestDrive == 2 && Helper.GPC.IsInVehicle())
                {
                    Helper.TestDrive += 1;
                }

                if (Helper.DrawSpotLight)
                {
                    World.DrawSpotLightWithShadow(Helper.VehPreviewPos + Vector3.WorldUp * 4f + Vector3.WorldNorth * 4f, Vector3.WorldSouth + Vector3.WorldDown, Color.White, 30f, 30f, 100f, 50f, -1);
                    World.DrawSpotLight(Helper.VehPreviewPos + Vector3.WorldUp * 4f + Vector3.WorldNorth * 4f, Vector3.WorldSouth + Vector3.WorldDown, Color.White, 30f, 30f, 100f, 50f, -1);
                }

                if (Game.IsControlJustPressed(Control.Context) && Helper.PdmDoorDist < 3.0f && !Helper.GPC.IsInVehicle() && Helper.GP.Wanted.WantedLevel == 0 && Helper.TaskScriptStatus == -1)
                {
                    Helper.TaskScriptStatus = 0;
                    Helper.HideHud = true;
                    Wait(200);
                    GtaScreen.FadeIn(200);
                    Helper.SelectedVehicle = Helper.optLastVehName;
                    Helper.PlayerLastPos = Helper.GPC.Position;
                    MenuHelper.CleanupVehicleViewerArea();
                    Helper.VehPreview?.Delete();
                    Helper.VehPreview = Helper.CreateVehicleFromHash(Helper.optLastVehHash, Helper.VehPreviewPos, 6.122209f);
                    Helper.UpdateVehPreview();
                    Helper.wsCamera.RepositionFor(Helper.VehPreview);
                    Helper.VehicleName = Helper.SelectedVehicle;
                    if (Helper.VehPreview != null && Helper.VehPreview.Exists())
                    {
                        Helper.VehiclePrice = MenuHelper.ResolveVehiclePrice(Helper.VehPreview.Model.Hash, Helper.VehicleName);
                    }
                    Helper.ShowVehicleName = true;
                    Helper.VehPreview.Heading = Helper.Radius;
                    Helper.VehPreview.LockStatus = VehicleLockStatus.IgnoredByPlayer;
                    Helper.VehPreview.DirtLevel = 0f;
                    MenuHelper.ShowOnly(MenuHelper.MainMenu);
                }

                if (MenuHelper._menuPool.AreAnyVisible)
                {
                    Helper.SuspendKeys();

                    if (Game.IsControlJustReleased(Helper.keyDoor) && Helper.TaskScriptStatus == 0)
                    {
                        if (Helper.VehPreview.Doors[VehicleDoorIndex.FrontLeftDoor].IsOpen)
                        {
                            Function.Call(Hash.SET_VEHICLE_DOORS_SHUT, Helper.VehPreview, false);
                        }
                        else
                        {
                            Helper.VehPreview.Doors[VehicleDoorIndex.BackLeftDoor].Open(false, false);
                            Helper.VehPreview.Doors[VehicleDoorIndex.BackRightDoor].Open(false, false);
                            Helper.VehPreview.Doors[VehicleDoorIndex.FrontLeftDoor].Open(false, false);
                            Helper.VehPreview.Doors[VehicleDoorIndex.FrontRightDoor].Open(false, false);
                            Helper.VehPreview.Doors[VehicleDoorIndex.Hood].Open(false, false);
                            Helper.VehPreview.Doors[VehicleDoorIndex.Trunk].Open(false, false);
                        }
                    }
                    else if (Game.IsControlJustPressed(Helper.keyRoof) && Helper.TaskScriptStatus == 0 && MenuHelper.IsPreviewVehicleConvertible())
                    {
                        if (Helper.VehPreview.RoofState == VehicleRoofState.Closed)
                        {
                            Function.Call(Hash.LOWER_CONVERTIBLE_ROOF, Helper.VehPreview, false);
                        }
                        else
                        {
                            Function.Call(Hash.RAISE_CONVERTIBLE_ROOF, Helper.VehPreview, false);
                        }
                    }

                    if (Game.IsControlJustReleased(Helper.keyCamera))
                    {
                        if (Helper.VehPreview.ClassType != VehicleClass.Motorcycles)
                        {
                            Helper.wsCamera.MainCameraPosition =
                                Helper.wsCamera.MainCameraPosition == CameraPosition.Car
                                    ? CameraPosition.Interior
                                    : CameraPosition.Car;
                        }
                    }

                    if (Game.IsControlJustPressed(Helper.keyZoom) && Helper.wsCamera.MainCameraPosition == CameraPosition.Car)
                    {
                        if (Math.Abs(Helper.wsCamera.CameraZoom - 5.0f) < 0.01f)
                        {
                            while (Helper.wsCamera.CameraZoom > 3.5f)
                            {
                                Yield();
                                Helper.wsCamera.CameraZoom -= 0.1f;
                            }
                        }
                        else
                        {
                            while (Helper.wsCamera.CameraZoom < 5.0f)
                            {
                                Yield();
                                Helper.wsCamera.CameraZoom += 0.1f;
                            }
                        }
                    }
                }

                if (Helper.blipName == "NULL")
                {
                    if (Helper.RequestAdditionTextFile("LFI_F"))
                    {
                        Helper.blipName = Gxt("collision_vt4m0x");
                        Helper.PdmBlip.Name = Helper.blipName;
                    }
                }

                Helper.PdmBlip.Alpha = (Game.IsMissionActive || Helper.GP.Wanted.WantedLevel > 1) ? 0 : 255;
            }
            catch (Exception ex)
            {
                logger.Log(ex.Message + " " + ex.StackTrace);
                logger.Log(Helper.blipName);
            }
        }

        public void OnAborted(object sender, EventArgs e)
        {
            try
            {
                Helper.PdmBlip?.Delete();
                GtaScreen.FadeIn(200);
                Helper.pdmPed?.Delete();
                pdmPedNextSeatRetryAt = 0;
                pdmPedNextGreetingAt = 0;
                clerkGreetedForCurrentVisit = false;
            }
            catch (Exception ex)
            {
                logger.Log(ex.Message + ex.StackTrace);
            }
        }
    }
}
