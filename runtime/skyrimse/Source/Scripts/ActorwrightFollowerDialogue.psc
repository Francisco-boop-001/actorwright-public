Scriptname ActorwrightFollowerDialogue extends TopicInfo Hidden
{
  Actorwright custom-voice follower dialogue fragments.

  One shared TopicInfo script is attached by VMAD to every action-bearing INFO
  that Actorwright writes for a custom-voiced follower. Each fragment delegates
  the follower state change to the vanilla DialogueFollowerScript on the
  DialogueFollower quest, so follower frameworks that hook the vanilla quest
  keep working and no Actorwright, XTTS, CHIM, or LLM service is needed at
  runtime.

  The script never polls, registers for updates, or keeps state.
}

DialogueFollowerScript Property DialogueFollower Auto
{ The vanilla DialogueFollower quest (Skyrim.esm). Bound by the VMAD property Actorwright writes. }

Function Fragment_Recruit(ObjectReference akSpeakerRef)
  DialogueFollower.SetFollower(akSpeakerRef)
EndFunction

Function Fragment_Dismiss(ObjectReference akSpeakerRef)
  ; iMessage 0 and iSayLine 0: the custom-voiced goodbye line was already spoken
  ; by this INFO, and the vanilla dismissal topic has no audio for this voice.
  DialogueFollower.DismissFollower(0, 0)
EndFunction

Function Fragment_Wait(ObjectReference akSpeakerRef)
  DialogueFollower.FollowerWait()
EndFunction

Function Fragment_Follow(ObjectReference akSpeakerRef)
  DialogueFollower.FollowerFollow()
EndFunction

Function Fragment_Trade(ObjectReference akSpeakerRef)
  Actor speaker = akSpeakerRef as Actor
  If speaker
    speaker.OpenInventory(true)
  EndIf
EndFunction
