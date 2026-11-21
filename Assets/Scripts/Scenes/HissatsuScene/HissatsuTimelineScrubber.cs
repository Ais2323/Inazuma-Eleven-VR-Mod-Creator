using UnityEngine;
using UnityEngine.EventSystems;

public sealed class HissatsuTimelineScrubber : MonoBehaviour,IPointerDownHandler,IPointerUpHandler,IBeginDragHandler,IEndDragHandler
{
    public HissatsuSceneController controller;
    bool held;
    public void OnPointerDown(PointerEventData e){if(e.button!=PointerEventData.InputButton.Left||held)return;held=true;controller.BeginScrub();}
    public void OnPointerUp(PointerEventData e){if(!held)return;held=false;controller.EndScrub();}
    public void OnBeginDrag(PointerEventData e)=>OnPointerDown(e);
    public void OnEndDrag(PointerEventData e)=>OnPointerUp(e);
    void OnDisable(){if(held){held=false;if(controller!=null)controller.EndScrub();}}
}
