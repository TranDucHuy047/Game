#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>

extern "C" {
    /// Returns NSProcessInfo.processInfo.thermalState as int:
    /// 0 = Nominal, 1 = Fair, 2 = Serious, 3 = Critical
    int _GetiOSThermalState() {
        if (@available(iOS 11.0, *)) {
            return (int)[[NSProcessInfo processInfo] thermalState];
        }
        return 0;
    }

    /// Triggers Apple Taptic Engine impact feedback:
    /// 0 = Light (harvest tap), 1 = Medium (combat hit), 2 = Heavy (kill / altar upgrade)
    void _TriggeriOSHapticFeedback(int style) {
        dispatch_async(dispatch_get_main_queue(), ^{
            if (@available(iOS 10.0, *)) {
                UIImpactFeedbackStyle impactStyle = UIImpactFeedbackStyleLight;
                if (style == 1) {
                    impactStyle = UIImpactFeedbackStyleMedium;
                } else if (style >= 2) {
                    impactStyle = UIImpactFeedbackStyleHeavy;
                }
                UIImpactFeedbackGenerator *generator = [[UIImpactFeedbackGenerator alloc] initWithStyle:impactStyle];
                [generator prepare];
                [generator impactOccurred];
            }
        });
    }
}
