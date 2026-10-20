"""End-to-end CPU worker checks. Needs FFmpeg and assets/text-removal-requirements.txt."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

sys.dont_write_bytecode = True

import cv2
import numpy as np

WORKER = Path(__file__).resolve().parents[1] / 'plugins/SchedularrTV.Plugins.GeneratedPromos/assets/text-removal.py'
spec = importlib.util.spec_from_file_location('cleanup', WORKER)
cleanup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cleanup)


class CleanupWorkerTests(unittest.TestCase):
    def frames(self):
        frames = np.full((30,180,320,4), 220, np.uint8)
        frames[:,:,:,3] = 255
        frames[:,:25,:25,3] = 0
        frames[:,25:35,:25,3] = 127
        for i, frame in enumerate(frames):
            cv2.putText(frame, 'TEST', (30+i*2,85), cv2.FONT_HERSHEY_SIMPLEX, .7, (15,15,15,255), 2)
        return frames

    def region(self, track=True):
        return dict(Name='Title',Start=0,End=2,Track=track,WholeRegion=False,StableSurface=True,Margin=3,
            Keys=[dict(At=10/15,X=44/320,Y=62/180,Width=72/320,Height=31/180)])

    def process(self, frames, regions):
        with tempfile.TemporaryDirectory(prefix='schedularrtv-cleanup-test-') as folder:
            root=Path(folder);source=root/'source.mkv';output=root/'clean.mkv';report=root/'tracking.json'
            subprocess.run(['ffmpeg','-v','error','-y','-f','rawvideo','-pix_fmt','bgra','-s','320x180','-r','15','-i','-',
                '-an','-c:v','ffv1','-pix_fmt','bgra',str(source)],input=frames.tobytes(),check=True)
            request=dict(Source=str(source),Output=str(output),Tracking=str(report),Regions=regions,Ffmpeg='ffmpeg',Ffprobe='ffprobe')
            path=root/'request.json';path.write_text(json.dumps(request))
            completed=subprocess.run([sys.executable,str(WORKER),str(path)],check=True,capture_output=True)
            raw=subprocess.check_output(['ffmpeg','-v','error','-i',str(output),'-f','rawvideo','-pix_fmt','bgra','-'])
            masks=subprocess.check_output(['ffmpeg','-v','error','-i',str(root/'mask.partial.mkv'),'-f','rawvideo','-pix_fmt','gray','-'])
            result=json.loads(report.read_text())
            result['_mask']=np.frombuffer(masks,np.uint8).reshape(-1,180,320)
            result['_progress']=completed.stdout.decode('utf-8')
            return np.frombuffer(raw,np.uint8).reshape(-1,180,320,4),result

    def test_tracks_moving_text_and_preserves_alpha_and_unselected_pixels(self):
        original=self.frames(); result,report=self.process(original,[self.region()])
        self.assertEqual(original.shape,result.shape)
        np.testing.assert_array_equal(original[:,:,:,3],result[:,:,:,3])
        np.testing.assert_array_equal(original[:,:45],result[:,:45])
        np.testing.assert_array_equal(original[:,120:],result[:,120:])
        self.assertEqual([],report['regions'][0]['untracked_frames'])
        before=np.count_nonzero(original[:,50:105,:,0]<60)
        after=np.count_nonzero(result[:,50:105,:,0]<60)
        self.assertLess(after,before*.08)
        anchor=report['regions'][0]['references'][0]
        self.assertEqual(10,anchor['backward_frames'])
        self.assertEqual(19,anchor['forward_frames'])
        self.assertIn('Tracking backward',report['_progress'])
        self.assertIn('Tracking forward',report['_progress'])
        self.assertTrue(report['_mask'][0].any())
        self.assertTrue(report['_mask'][-1].any())
        self.assertFalse(report['_mask'][:,:45].any())

    def test_scene_cut_is_reported_and_untracked_frames_remain_untouched(self):
        original=self.frames()
        rng=np.random.default_rng(123)
        original[15:,:,:,:3]=rng.integers(0,100,(15,180,320,3),dtype=np.uint8)
        result,report=self.process(original,[self.region()])
        skipped=report['regions'][0]['untracked_frames']
        self.assertGreaterEqual(len(skipped),14)
        for frame in skipped:
            np.testing.assert_array_equal(original[frame],result[frame])

    def test_stationary_region_only_changes_active_interval(self):
        original=self.frames(); original[:]=original[10]
        region=self.region(False);region['Start']=.4;region['End']=1.2
        result,_=self.process(original,[region])
        np.testing.assert_array_equal(original[:6],result[:6])
        np.testing.assert_array_equal(original[19:],result[19:])
        self.assertGreater(np.count_nonzero(original[10]!=result[10]),50)

    def test_correction_reacquires_after_a_cut(self):
        original=self.frames()
        for i in range(15,30):
            original[i,:,:,:3]=170
            cv2.putText(original[i],'TEST',(190,145),cv2.FONT_HERSHEY_SIMPLEX,.7,(15,15,15,255),2)
        region=self.region();region['Keys'].append(dict(At=20/15,X=184/320,Y=122/180,Width=72/320,Height=31/180))
        result,report=self.process(original,[region])
        self.assertEqual([],report['regions'][0]['untracked_frames'])
        self.assertLess(np.count_nonzero(result[20,120:155,180:265,0]<60),10)
        by_frame={f['frame']:f for f in report['regions'][0]['frames']}
        self.assertEqual(10,by_frame[10]['reference'])
        self.assertEqual(20,by_frame[20]['reference'])
        np.testing.assert_array_equal(by_frame[20]['matrix'],np.eye(3))

    def test_auto_detection_finds_dark_letters_on_mid_dark_paper(self):
        image=np.full((180,320,3),100,np.uint8)
        cv2.putText(image,'TEST',(50,85),cv2.FONT_HERSHEY_SIMPLEX,.7,(10,10,10),2)
        region=self.region(); key=region['Keys'][0]
        mask=cleanup.lettering(image,key,region)
        ink=image[:,:,0]<30
        self.assertGreater(np.mean(mask[ink]>0),.95)

    def test_bright_slanted_paper_edge_does_not_reverse_ink_detection(self):
        image=np.full((180,320,3),35,np.uint8)
        cv2.fillPoly(image,[np.array([[25,45],[265,15],[280,125],[40,155]],np.int32)],(220,220,220))
        cv2.putText(image,'TEST',(70,95),cv2.FONT_HERSHEY_SIMPLEX,1,(10,10,10),2)
        region=self.region()
        key=dict(At=0,X=40/320,Y=35/180,Width=240/320,Height=100/180)
        mask=cleanup.lettering(image,key,region)
        ink=image[:,:,0]==10
        self.assertGreater(np.mean(mask[ink]>0),.95)

    def test_sensitivity_can_include_faint_letters_and_whole_box_covers_every_pixel(self):
        image=np.full((180,320,3),220,np.uint8)
        cv2.putText(image,'TEST',(50,85),cv2.FONT_HERSHEY_SIMPLEX,.7,(206,206,206),2)
        region=self.region();region['Margin']=0;region['Ink']=1
        region['Sensitivity']=0
        conservative=cleanup.lettering(image,region['Keys'][0],region)
        region['Sensitivity']=100
        sensitive=cleanup.lettering(image,region['Keys'][0],region)
        self.assertGreater(np.count_nonzero(sensitive),np.count_nonzero(conservative)+100)
        region['WholeRegion']=True
        whole=cleanup.lettering(image,region['Keys'][0],region)
        self.assertTrue(np.all(whole[63:92,45:115]==255))


if __name__=='__main__': unittest.main()
